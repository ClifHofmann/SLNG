# FEAT-PERF-14: does GeometryInstance3D.visibility_range_end cut draws in the main AND the shadow pass on this
# engine build, and by how much for a club-like mix of sizes?
#
# Standalone (not part of the viewer). Run it windowed:
#   godot --path tools/visrange-probe -- [small=4000] [medium=1500] [large=500] [px=3]
# A grid of cubes sits in front of the camera between 5 m and 105 m: many small decor prims, fewer mid-size,
# a few big. One MeshInstance3D each, one shared mesh and material, shadows on, a 2-split sun - the same shape
# as the viewer's scene. It prints the draw calls of the camera pass (VISIBLE) and the sun's shadow pass (SHADOW)
# with no ranges, with the ranges SLNG.Core.SmallObjectCull gives (re-implemented below, same arithmetic), and
# with every range forced to a fixed 40 m, so the engine's behaviour can be told from the rule's.
extends Node

const FOV_DEG := 60.0

var _nodes: Array[MeshInstance3D] = []
var _radii: PackedFloat32Array = PackedFloat32Array()
var _px := 3.0
var _ppr := 0.0


func lod_scale(f: float) -> float:
	var ff := minf(f, 8.0)
	return 0.9 * ff / maxf(1.0 - ff * 0.1, 0.1)


# Same arithmetic as SmallObjectCull.EndDistance (px at LOD factor 1, 15 m floor, 1.5 m never, 0 = none).
func end_distance(radius: float, px: float, lod: float, hide: float) -> float:
	if px <= 0.0 or radius <= 0.0 or radius >= 1.5:
		return 0.0
	var d := radius / tan(px / _ppr) * lod_scale(lod)
	d = maxf(d, 15.0)
	return 0.0 if d >= hide else d


func apply(mode: String, lod: float, hide: float) -> void:
	for i in _nodes.size():
		var end := 0.0
		match mode:
			"rule": end = end_distance(_radii[i], _px, lod, hide)
			"fixed40": end = 40.0
		_nodes[i].visibility_range_end = end
		_nodes[i].visibility_range_end_margin = maxf(end * 0.1, 1.0) if end > 0.0 else 0.0


func sample(label: String) -> void:
	for i in 8:
		await get_tree().process_frame
	var vis := 0.0
	var sh := 0.0
	var tot := 0.0
	var n := 12
	var rid := get_viewport().get_viewport_rid()
	for i in n:
		await get_tree().process_frame
		vis += RenderingServer.viewport_get_render_info(rid, RenderingServer.VIEWPORT_RENDER_INFO_TYPE_VISIBLE, RenderingServer.VIEWPORT_RENDER_INFO_DRAW_CALLS_IN_FRAME)
		sh += RenderingServer.viewport_get_render_info(rid, RenderingServer.VIEWPORT_RENDER_INFO_TYPE_SHADOW, RenderingServer.VIEWPORT_RENDER_INFO_DRAW_CALLS_IN_FRAME)
		tot += Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME)
	print("   (frames drawn %d, objects in frame %.0f, primitives %.0f)" % [Engine.get_frames_drawn(), Performance.get_monitor(Performance.RENDER_TOTAL_OBJECTS_IN_FRAME), Performance.get_monitor(Performance.RENDER_TOTAL_PRIMITIVES_IN_FRAME)])
	print("%-34s drawsScene %6.0f  drawsShadow %6.0f  total %6.0f  fps %4.0f" % [label, vis / n, sh / n, tot / n, Performance.get_monitor(Performance.TIME_FPS)])


func _ready() -> void:
	var args := OS.get_cmdline_user_args()
	var small := int(args[0]) if args.size() > 0 else 4000
	var medium := int(args[1]) if args.size() > 1 else 1500
	var large := int(args[2]) if args.size() > 2 else 500
	_px = float(args[3]) if args.size() > 3 else 3.0

	var vp_h := float(get_viewport().get_visible_rect().size.y)
	_ppr = vp_h / deg_to_rad(FOV_DEG)

	var cam := Camera3D.new()
	cam.fov = FOV_DEG
	cam.far = 400.0
	add_child(cam)
	cam.position = Vector3(0, 1.7, 0)

	var sun := DirectionalLight3D.new()
	sun.shadow_enabled = true
	sun.directional_shadow_mode = DirectionalLight3D.SHADOW_PARALLEL_2_SPLITS
	sun.directional_shadow_max_distance = 90.0
	sun.rotation_degrees = Vector3(-50, 30, 0)
	add_child(sun)

	var env := WorldEnvironment.new()
	env.environment = Environment.new()
	add_child(env)

	var floor_mesh := MeshInstance3D.new()
	var plane := PlaneMesh.new()
	plane.size = Vector2(600, 600)
	floor_mesh.mesh = plane
	add_child(floor_mesh)

	var box := BoxMesh.new()
	box.size = Vector3.ONE
	var rng := RandomNumberGenerator.new()
	rng.seed = 14

	var sizes := [[small, 0.2], [medium, 0.5], [large, 2.0]]
	for group in sizes:
		for k in int(group[0]):
			var m := MeshInstance3D.new()
			m.mesh = box
			# One material per object: Forward+ merges consecutive draws of the same mesh AND material into
			# one instanced call, which would hide exactly the cost being measured. The viewer's prims have
			# their own material each (a face's texture, colour, glow), so they do not merge either.
			var own := StandardMaterial3D.new()
			own.albedo_color = Color(rng.randf(), rng.randf(), rng.randf())
			m.material_override = own
			var s: float = group[1]
			m.scale = Vector3(s, s, s)
			var d := rng.randf_range(5.0, 105.0)
			var half_w := d * tan(deg_to_rad(FOV_DEG * 0.5)) * 1.4
			m.position = Vector3(rng.randf_range(-half_w, half_w), rng.randf_range(0.1, 3.0), -d)
			add_child(m)
			_nodes.append(m)
			_radii.append(s * sqrt(3.0) * 0.5)

	print("--- FEAT-PERF-14 visibility-range probe: %d small (0.2 m) + %d medium (0.5 m) + %d large (2 m), cull %.1f px, ppr %.0f, Godot %s" % [small, medium, large, _px, _ppr, Engine.get_version_info().string])

	apply("none", 1.0, 110.0)
	await sample("no ranges")
	apply("rule", 1.0, 110.0)
	await sample("rule, LOD 1.0 (Low), hide 110 m")
	apply("rule", 2.0, 202.0)
	await sample("rule, LOD 2.0 (High), hide 202 m")
	apply("rule", 3.0, 294.0)
	await sample("rule, LOD 3.0 (Ultra), hide 294 m")
	apply("fixed40", 1.0, 110.0)
	await sample("every range forced to 40 m")
	apply("none", 1.0, 110.0)
	await sample("no ranges (again)")
	get_tree().quit()
