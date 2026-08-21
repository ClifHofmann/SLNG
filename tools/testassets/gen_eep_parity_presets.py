#!/usr/bin/env python3
"""Generate the EEP parity probe presets (FEAT-ENV-01).

Writes legacy Windlight XML presets into eep-parity/, each one the viewer's own
Default.xml with EXACTLY ONE value changed. The protocol that uses them is
docs/eep-parity-probe-protocol.md.

Legacy Windlight XML, not EEP LLSD, because that is what the environment editor's
Import button accepts: LLFloaterFixedEnvironment::doImportFromDisk is commented
"Load a legacy Windlight XML from disk" and routes through
createSkyFromLegacyPreset / createWaterFromLegacyPreset.

The two base tables below are transcribed verbatim from
  indra/newview/app_settings/windlight/skies/Default.xml
  indra/newview/app_settings/windlight/water/Default.xml
so the neutral baseline is Linden's own default rather than anything we picked.
Do not "tidy" the values -- the long decimals are what the viewer ships, and
keeping them byte-comparable is the point.

Format notes worth not rediscovering:
  * Most scalars are 4-element arrays with the value in [0]; a few
    (east_angle, sun_angle, star_brightness) are bare reals.
  * cloud_scroll_rate is stored OFFSET BY +10 -- the default 10.2/10.011 means a
    real rate of 0.2/0.011. The viewer subtracts 10 in translateLegacySettings.
  * sun_angle is the sun's ALTITUDE in radians; east_angle is the azimuth, negated
    on import. The moon is placed diametrically opposite the sun automatically.
  * Water keys are camelCase in this format (waterFogDensity, normScale, ...).

Run from anywhere:  python tools/testassets/gen_eep_parity_presets.py
"""

import copy
import os

OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "eep-parity")


def R(v):
    return ("real", v)


def A(*v):
    return ("array", [R(x) for x in v])


def AB(*v):
    return ("array", [("boolean", 1 if x else 0) for x in v])


def AI(*v):
    return ("array", [("integer", x) for x in v])


def U(v):
    return ("uuid", v)


SKY = [
    ("ambient", A(1.0499999523162842, 1.0499999523162842, 1.0499999523162842, 0.34999999403953552)),
    ("blue_density", A(0.24475815892219543, 0.44872328639030457, 0.75999999046325684, 0.37999999523162842)),
    ("blue_horizon", A(0.49548381567001343, 0.49548381567001343, 0.63999998569488525, 0.31999999284744263)),
    ("cloud_color", A(0.40999999642372131, 0.40999999642372131, 0.40999999642372131, 0.40999999642372131)),
    ("cloud_pos_density1", A(1.6884100437164307, 0.52609699964523315, 1, 1)),
    ("cloud_pos_density2", A(1.6884100437164307, 0.52609699964523315, 0.125, 1)),
    ("cloud_scale", A(0.41999998688697815, 0, 0, 1)),
    ("cloud_scroll_rate", A(10.199999809265137, 10.01099967956543)),
    ("cloud_shadow", A(0.26999998092651367, 0, 0, 1)),
    ("density_multiplier", A(0.00017999998817685992, 0, 0, 1)),
    ("distance_multiplier", A(0.80000001192092896, 0, 0, 1)),
    ("east_angle", R(0)),
    ("enable_cloud_scroll", AB(True, True)),
    ("gamma", A(1, 0, 0, 1)),
    ("glow", A(5, 0.0010000000474974513, -0.47999998927116394, 1)),
    ("haze_density", A(0.69999998807907104, 0, 0, 1)),
    ("haze_horizon", A(0.18999999761581421, 0.19915600121021271, 0.19915600121021271, 1)),
    ("max_y", A(1605, 0, 0, 1)),
    ("star_brightness", R(0)),
    ("sun_angle", R(1.9917697906494141)),
    ("sunlight_color", A(0.7342105507850647, 0.78157895803451538, 0.89999997615814209, 0.29999998211860657)),
]

WATER = [
    ("blurMultiplier", R(0.040000002831220627)),
    ("fresnelOffset", R(0.5)),
    ("fresnelScale", R(0.39999997615814209)),
    ("normScale", AI(2, 2, 2)),
    ("normalMap", U("822ded49-9a6c-f61c-cb89-6df54f42cdf4")),
    ("scaleAbove", R(0.029999999329447746)),
    ("scaleBelow", R(0.20000000298023224)),
    ("underWaterFogMod", R(0.25)),
    ("waterFogColor", A(0.015686275437474251, 0.14901961386203766, 0.25098040699958801, 1)),
    ("waterFogDensity", R(16)),
    ("wave1Dir", A(1.0499997138977051, -0.42000007629394531)),
    ("wave2Dir", A(1.1099996566772461, -1.1600000858306885)),
]


def render(pairs):
    out = ["<llsd>", "    <map>"]
    for key, (kind, payload) in pairs:
        out.append("    <key>%s</key>" % key)
        if kind == "array":
            out.append("        <array>")
            for t, pv in payload:
                out.append("            <%s>%s</%s>" % (t, pv, t))
            out.append("        </array>")
        else:
            out.append("        <%s>%s</%s>" % (kind, payload, kind))
    out += ["    </map>", "</llsd>", ""]
    return "\n".join(out)


def variant(base, name, **over):
    pairs = copy.deepcopy(base)
    index = {k: i for i, (k, _) in enumerate(pairs)}
    for key, value in over.items():
        if key not in index:
            raise KeyError("%s is not a key in the base preset" % key)
        pairs[index[key]] = (key, value)
    path = os.path.join(OUT_DIR, name + ".xml")
    with open(path, "w", newline="\n") as handle:
        handle.write(render(pairs))
    return name


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    # Values sit at or near the viewer's own validator limits on purpose: a parameter
    # at its extreme produces an unmistakable difference, one at 60% produces an argument.
    written = [
        variant(SKY, "PARITY-00-neutral"),
        variant(SKY, "PARITY-01-haze-max", haze_density=A(5, 0, 0, 1)),
        variant(SKY, "PARITY-02-haze-min", haze_density=A(0, 0, 0, 1)),
        variant(SKY, "PARITY-03-bluedens-max", blue_density=A(3, 3, 3, 1)),
        variant(SKY, "PARITY-04-densmult-max", density_multiplier=A(2.0, 0, 0, 1)),
        variant(SKY, "PARITY-05-distmult-max", distance_multiplier=A(100, 0, 0, 1)),
        variant(SKY, "PARITY-06-maxy-low", max_y=A(500, 0, 0, 1)),
        variant(SKY, "PARITY-07-glow-tight", glow=A(40, 0.0010000000474974513, -0.47999998927116394, 1)),
        variant(SKY, "PARITY-08-glow-wide", glow=A(0.2, 0.0010000000474974513, -0.47999998927116394, 1)),
        variant(SKY, "PARITY-09-sun-low", sun_angle=R(0.1)),
        variant(SKY, "PARITY-10-sun-high", sun_angle=R(1.4)),
        variant(SKY, "PARITY-11-cloudcover-max", cloud_shadow=A(1.0, 0, 0, 1)),
        variant(SKY, "PARITY-12-cloudcover-min", cloud_shadow=A(0.25, 0, 0, 1)),
        variant(SKY, "PARITY-14-cloudscale-small", cloud_scale=A(0.1, 0, 0, 1)),
        variant(SKY, "PARITY-15-stars-max", star_brightness=R(500), sun_angle=R(3.4)),
        variant(SKY, "PARITY-16-night", sun_angle=R(3.4)),
        variant(SKY, "PARITY-18-scroll-fast", cloud_scroll_rate=A(30.0, 10.01099967956543)),
        variant(SKY, "PARITY-19-scroll-off", enable_cloud_scroll=AB(False, False)),
        # Coverage probes. Two changed keys ONLY -- cloud_color and cloud_shadow -- because
        # the sky must keep rendering exactly as in PARITY-00-neutral. An earlier attempt
        # zeroed ambient/blue_horizon/blue_density/haze_horizon/haze_density as well to get
        # a black backdrop, and Firestorm then drew no clouds at all under any cover value.
        # Six changed parameters make that uninterpretable: it could be a real divergence or
        # a side effect of flattening its sky. Single-parameter isolation is the whole point
        # of this protocol and that probe broke it.
        #
        # A BLACK cloud_color does not work either: oHazeColorBelowCloud (cloudsF.glsl:182)
        # is added AFTER the cloud_color multiply, so a zeroed colour leaves the cloud
        # showing the haze colour -- i.e. the sky's own colour. Both viewers went blank.
        #
        # WHITE is the one that survives both traps: it is unaffected by the haze-bleed term
        # and it reads clearly against the Default sky's pale blue.
        variant(SKY, "PARITY-20-cover-000", cloud_color=A(1, 1, 1, 1), cloud_shadow=A(0.25, 0, 0, 1)),
        variant(SKY, "PARITY-21-cover-050", cloud_color=A(1, 1, 1, 1), cloud_shadow=A(0.50, 0, 0, 1)),
        variant(SKY, "PARITY-22-cover-100", cloud_color=A(1, 1, 1, 1), cloud_shadow=A(1.00, 0, 0, 1)),
        variant(WATER, "PARITY-W0-neutral"),
        variant(WATER, "PARITY-W1-fogdens-max", waterFogDensity=R(100)),
        variant(WATER, "PARITY-W2-fogdens-min", waterFogDensity=R(0.001)),
        variant(WATER, "PARITY-W3-fresnel-max", fresnelScale=R(1.0)),
        variant(WATER, "PARITY-W4-blur-neg", blurMultiplier=R(-0.5)),
        variant(WATER, "PARITY-W5-normscale-max", normScale=AI(10, 10, 10)),
        variant(WATER, "PARITY-W6-wave-fast", wave1Dir=A(20.0, 20.0), wave2Dir=A(-20.0, 20.0)),
    ]

    # 13 and 17 are deliberately absent: cloud_variance and moon_brightness have no
    # legacy key at all, so those two have to be set in the editor after importing
    # their neighbour. Same for W7, which is just W0 with the camera underwater.
    print("%d presets written to %s" % (len(written), OUT_DIR))


if __name__ == "__main__":
    main()
