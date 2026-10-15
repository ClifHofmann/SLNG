# BUG-RENDER-48 probe: how do things BELOW the water surface look, opaque vs alpha-blended?
#
# Standalone (not part of the viewer); it uses the viewer's own shaders, copied in by run.ps1 (a stale copy would
# measure yesterday's shader). Run it WINDOWED -- headless has no GPU:
#
#   tools/waterfog-probe/run.ps1 [-Density 2] [-Eye 3.0] [-Mode both|opaque|blend|refl]
#
# Scene: a sandy sea floor 5 m down, the viewer's water.gdshader on a plane at y = 0 (flat normal map), SLNG's own
# sky.gdshader overhead, and seven green poles standing from the floor to 2 m above the water. In `both`, the four
# on the left (x <= 0) are prim_opaque, the three on the right prim_blend. It publishes the same globals
# EnvironmentDriver / AvatarController do and prints the sRGB of each pole 1 m above and 0.6 / 2 / 4 m below the
# surface -- an alpha-1 blended pole should read like the opaque one beside it. `refl` puts black slabs under the
# water instead (left opaque, right blend) so that what is left is only the surface's own reflection: the right
# slab's colour is slng_water_surface_reflection, the left slab's is what Godot's reflection really gives.
#
#   density=<f>  the water fog density (0 switches the fog off: the fresnel-only baseline)
#   eye=<f>      camera height above the water; 3.0 looks ~30 deg down at the poles, 1.6 ~13 deg (a beach)
#   sunz=<f>     sun elevation (sine), for waterFogKS
#   shot=<png>   also save the frame
extends Node3D

var args := {}
var cam: Camera3D

func _ready() -> void:
	for a in OS.get_cmdline_user_args():
		var kv := a.split("=")
		if kv.size() == 2:
			args[kv[0]] = kv[1]
	var density := float(args.get("density", "2.0"))
	var eye := float(args.get("eye", "3.0"))
	var sun_z := float(args.get("sunz", "0.707"))

	var env := Environment.new()
	var sky := Sky.new()
	var skymat := ShaderMaterial.new()
	skymat.shader = load("res://materials/sky.gdshader")
	sky.sky_material = skymat
	env.sky = sky
	env.background_mode = Environment.BG_SKY
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(0.6, 0.6, 0.65)
	env.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	var we := WorldEnvironment.new()
	we.environment = env
	add_child(we)

	var sun := DirectionalLight3D.new()
	sun.rotation_degrees = Vector3(-50, 30, 0)
	sun.light_energy = 1.0
	add_child(sun)

	cam = Camera3D.new()
	cam.position = Vector3(0, eye, 16)
	add_child(cam)
	cam.look_at(Vector3(0, -2.0, 0))

	# sea floor
	var floor_mesh := MeshInstance3D.new()
	var pm := PlaneMesh.new()
	pm.size = Vector2(120, 120)
	floor_mesh.mesh = pm
	floor_mesh.position = Vector3(0, -5, 0)
	var fm := StandardMaterial3D.new()
	fm.albedo_color = Color(0.76, 0.65, 0.45)
	floor_mesh.material_override = fm
	add_child(floor_mesh)

	# water
	var water := MeshInstance3D.new()
	var wp := PlaneMesh.new()
	wp.size = Vector2(160, 160)
	wp.subdivide_width = 64
	wp.subdivide_depth = 64
	water.mesh = wp
	var wmat := ShaderMaterial.new()
	wmat.shader = load("res://materials/water.gdshader")
	wmat.render_priority = -1
	var img := Image.create(64, 64, false, Image.FORMAT_RGB8)
	img.fill(Color(0.5, 0.5, 1.0))
	wmat.set_shader_parameter("slng_water_normal_map", ImageTexture.create_from_image(img))
	water.material_override = wmat
	add_child(water)

	if String(args.get("mode", "both")) == "refl":
		fm.albedo_color = Color(0, 0, 0)
		for side in [-1.0, 1.0]:
			var slab := MeshInstance3D.new()
			var sm := BoxMesh.new()
			sm.size = Vector3(60, 0.1, 100)
			slab.mesh = sm
			slab.position = Vector3(side * 30.0 + side * 0.05, -1.0, 0)
			var m2 := ShaderMaterial.new()
			m2.shader = load("res://materials/prim/prim_blend.gdshader") if side > 0 else load("res://materials/prim/prim_opaque.gdshader")
			m2.set_shader_parameter("albedo_color", Color(0, 0, 0, 1))
			slab.material_override = m2
			add_child(slab)

	# poles: x = -9..9. Left half opaque, right half blend. Heights floor(-5) .. +2.
	var mode := String(args.get("mode", "both"))
	var opaque_sh: Shader = load("res://materials/prim/prim_opaque.gdshader")
	var blend_sh: Shader = load("res://materials/prim/prim_blend.gdshader")
	for i in (0 if String(args.get("mode", "both")) == "refl" else 7):
		var x := -9.0 + i * 3.0
		var is_blend := x > 0.0 if mode == "both" else mode == "blend"
		var pole := MeshInstance3D.new()
		var bm := BoxMesh.new()
		bm.size = Vector3(0.9, 7.0, 0.9)
		pole.mesh = bm
		pole.position = Vector3(x, -1.5, 0)
		var m := ShaderMaterial.new()
		m.shader = blend_sh if is_blend else opaque_sh
		m.set_shader_parameter("albedo_color", Color(0.1, 0.9, 0.1, 1.0))
		pole.material_override = m
		add_child(pole)

	# the shared globals, as EnvironmentDriver / AvatarController publish them
	var fog_srgb := Color(0.0156, 0.1490, 0.2509)
	var fog_lin := fog_srgb.srgb_to_linear()
	RenderingServer.global_shader_parameter_set("slng_water_fog_color", Vector3(fog_lin.r, fog_lin.g, fog_lin.b))
	RenderingServer.global_shader_parameter_set("slng_water_fog_density", density)
	RenderingServer.global_shader_parameter_set("slng_water_fresnel", Vector2(0.3999, 0.5))
	RenderingServer.global_shader_parameter_set("slng_water_fog_ks", 1.0 / maxf(sun_z, 0.3))
	RenderingServer.global_shader_parameter_set("slng_sun_direction", Vector3(0.3, sun_z, 0.2).normalized())
	RenderingServer.global_shader_parameter_set("slng_sun_direction_view", (cam.global_transform.basis.inverse() * Vector3(0.3, sun_z, 0.2).normalized()))
	RenderingServer.frame_pre_draw.connect(_publish_plane)
	_run()

func _publish_plane() -> void:
	var up := cam.global_transform.basis.inverse() * Vector3.UP
	RenderingServer.global_shader_parameter_set("slng_water_plane_view", Color(up.x, up.y, up.z, cam.global_transform.origin.y - 0.0))

func _run() -> void:
	for i in 12:
		await get_tree().process_frame
	var out := String(args.get("shot", ""))
	var im := get_viewport().get_texture().get_image()
	if out != "":
		im.save_png(out)
	# sample the poles: a point 0.5 m above and 1 m / 3 m below the water on each pole
	var labels := PackedStringArray()
	for i in 7:
		var x := -9.0 + i * 3.0
		for dy in [1.0, -0.6, -2.0, -4.0]:
			var sp := cam.unproject_position(Vector3(x, dy, 0.46))
			var c := im.get_pixelv(Vector2i(sp))
			labels.append("x=%+.0f y=%+.1f -> srgb(%.3f %.3f %.3f)" % [x, dy, c.r, c.g, c.b])
	if String(args.get("mode", "both")) == "refl":
		for dz in [10.0, 6.0, 2.0, -4.0, -20.0]:
			var pl := cam.unproject_position(Vector3(-4.0, -1.0, dz))
			var pr := cam.unproject_position(Vector3(4.0, -1.0, dz))
			var cl := im.get_pixelv(Vector2i(pl))
			var cr := im.get_pixelv(Vector2i(pr))
			print("REFL dz=%+.0f  opaque(R actual) srgb(%.3f %.3f %.3f)  blend(R mine) srgb(%.3f %.3f %.3f)" % [dz, cl.r, cl.g, cl.b, cr.r, cr.g, cr.b])
	print("PROBE ", "density=", args.get("density", "2.0"), " eye=", args.get("eye", "3.0"))
	for l in labels:
		print("PROBE ", l)
	get_tree().quit()
