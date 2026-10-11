# BUG-PERF-19: what the process keeps when Godot frees video memory.
#
# Standalone (not part of the viewer). Run it windowed on the card you want to measure:
#   godot --path tools/vram-probe -- [slack] [staged]
# Every line is  Godot's own figure (Performance RENDER_VIDEO_MEM_USED = VMA allocationBytes) against the process's
# dedicated usage from the Windows counter "GPU Process Memory(pid_N*)" -- the same number DXGI QueryVideoMemoryInfo
# gives -- and the whole adapter. "slack" first fills the card with 600 textures interleaved with small meshes and
# frees the textures (the allocator is left holding blocks); "staged" switches the 1440p buffers Ultra <-> High through a
# buffer-free state (what RenderBufferSwitch does) instead of directly. Windows + PowerShell only (OS.execute).
# Needs ~3 GB of free video memory with "slack". Results of 2026-10-11 (RTX 4070): docs/specs/BUG-PERF-19-*.md.
extends Node


func proc_mb() -> int:
	var o := []
	var cmd := "$s=0; (Get-Counter '\\GPU Process Memory(pid_%d*)\\Dedicated Usage' -ErrorAction SilentlyContinue).CounterSamples | ForEach-Object { $s += $_.CookedValue }; [math]::Round($s/1MB)" % OS.get_process_id()
	OS.execute("powershell", ["-NoProfile", "-Command", cmd], o)
	return int(String(o[0]).strip_edges()) if o.size() > 0 else -1

func adapter_mb() -> int:
	var o := []
	OS.execute("powershell", ["-NoProfile", "-Command", "$s=0; (Get-Counter '\\GPU Adapter Memory(*)\\Dedicated Usage').CounterSamples | ForEach-Object { $s += $_.CookedValue }; [math]::Round($s/1MB)"], o)
	return int(String(o[0]).strip_edges()) if o.size() > 0 else -1

func report(label: String) -> void:
	for i in 12:
		await get_tree().process_frame
	await get_tree().create_timer(1.5).timeout
	var v := int(Performance.get_monitor(Performance.RENDER_VIDEO_MEM_USED) / 1048576.0)
	var t := int(Performance.get_monitor(Performance.RENDER_TEXTURE_MEM_USED) / 1048576.0)
	var b := int(Performance.get_monitor(Performance.RENDER_BUFFER_MEM_USED) / 1048576.0)
	var p := proc_mb()
	var line := "%-46s godot video %5d  tex %5d  buf %5d | process %5d | process-godot %5d | adapter %5d" % [label, v, t, b, p, p - v, adapter_mb()]
	print(line)

func make_tex(img: Image) -> ImageTexture:
	return ImageTexture.create_from_image(img)

func make_mesh(verts: int) -> ArrayMesh:
	var arr := []
	arr.resize(Mesh.ARRAY_MAX)
	var pv := PackedVector3Array()
	var pn := PackedVector3Array()
	var pi := PackedInt32Array()
	pv.resize(verts); pn.resize(verts)
	for i in verts:
		pv[i] = Vector3(i, i * 0.5, 0)
		pn[i] = Vector3.UP
	for i in range(0, verts - 2):
		pi.append(i); pi.append(i + 1); pi.append(i + 2)
	arr[Mesh.ARRAY_VERTEX] = pv
	arr[Mesh.ARRAY_NORMAL] = pn
	arr[Mesh.ARRAY_INDEX] = pi
	var m := ArrayMesh.new()
	m.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arr)
	return m

func set_ultra(sv: SubViewport, env: Environment) -> void:
	sv.msaa_3d = Viewport.MSAA_8X
	env.ssao_enabled = true
	env.ssil_enabled = true
	env.ssr_enabled = true
	env.glow_enabled = true

func set_high(sv: SubViewport, env: Environment) -> void:
	sv.msaa_3d = Viewport.MSAA_4X
	env.ssao_enabled = true
	env.ssil_enabled = false
	env.ssr_enabled = true
	env.glow_enabled = true

func set_minimal(sv: SubViewport, env: Environment) -> void:
	sv.msaa_3d = Viewport.MSAA_DISABLED
	env.ssao_enabled = false
	env.ssil_enabled = false
	env.ssr_enabled = false
	env.glow_enabled = false

func frames(n: int) -> void:
	for i in n:
		await get_tree().process_frame

func _ready() -> void:
	var args := OS.get_cmdline_user_args()
	var slack := "slack" in args
	var staged := "staged" in args
	var tag := ("slack " if slack else "") + ("staged" if staged else "direct")
	print("scenario: ", tag, " adapter ", RenderingServer.get_video_adapter_name())
	await get_tree().create_timer(1.0).timeout
	await report("%s: baseline" % tag)

	var keep_meshes: Array = []
	if slack:
		var big := Image.create_empty(1024, 1024, true, Image.FORMAT_RGBA8)
		big.fill(Color(0.3, 0.5, 0.7))
		big.generate_mipmaps()
		var mid := Image.create_empty(512, 512, true, Image.FORMAT_RGBA8)
		mid.fill(Color(0.7, 0.5, 0.3))
		mid.generate_mipmaps()
		var texs: Array = []
		for i in 600:
			texs.append(make_tex(big if i % 3 == 0 else mid))
			if i % 2 == 0:
				keep_meshes.append(make_mesh(2000))
			if i % 5 == 0:
				keep_meshes.append(make_mesh(30))
			if i % 50 == 49:
				await get_tree().process_frame
		await report("%s: 600 textures + meshes" % tag)
		texs.clear()
		await report("%s: textures freed, meshes kept (slack)" % tag)

	var sv := SubViewport.new()
	sv.size = Vector2i(2560, 1440)
	sv.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	add_child(sv)
	var cam := Camera3D.new()
	cam.position = Vector3(0, 2, 6)
	sv.add_child(cam)
	var sun := DirectionalLight3D.new()
	sun.shadow_enabled = true
	sun.directional_shadow_mode = DirectionalLight3D.SHADOW_PARALLEL_2_SPLITS
	sun.rotation_degrees = Vector3(-50, 30, 0)
	sv.add_child(sun)
	var env := Environment.new()
	env.background_mode = Environment.BG_SKY
	env.sky = Sky.new()
	env.sky.sky_material = ProceduralSkyMaterial.new()
	var we := WorldEnvironment.new()
	we.environment = env
	sv.add_child(we)
	for i in 30:
		var mi := MeshInstance3D.new()
		mi.mesh = SphereMesh.new()
		mi.position = Vector3(i % 6 - 3, 0.5, -i / 6.0)
		sv.add_child(mi)
	RenderingServer.directional_shadow_atlas_set_size(4096, true)
	set_ultra(sv, env)
	await report("%s: ultra buffers (1440p MSAA8 ssao ssil ssr glow)" % tag)
	for cycle in 3:
		if staged:
			set_minimal(sv, env)
			await frames(8)
		set_high(sv, env)
		await report("%s: %d -> high" % [tag, cycle + 1])
		if staged:
			set_minimal(sv, env)
			await frames(8)
		set_ultra(sv, env)
		await report("%s: %d -> ultra" % [tag, cycle + 1])
	sv.queue_free()
	await report("%s: viewport freed" % tag)
	keep_meshes.clear()
	await report("%s: meshes freed" % tag)
	get_tree().quit()
