default
{
    state_entry()
    {
        // Simple fountain effect
        llParticleSystem([
            PSYS_PART_FLAGS, PSYS_PART_INTERP_COLOR_MASK | PSYS_PART_INTERP_SCALE_MASK | PSYS_PART_EMISSIVE_MASK,
            PSYS_SRC_PATTERN, PSYS_SRC_PATTERN_EXPLODE,
            PSYS_PART_MAX_AGE, 3.0,
            PSYS_PART_START_COLOR, <1.0, 0.5, 0.0>,
            PSYS_PART_END_COLOR, <1.0, 0.0, 0.0>,
            PSYS_PART_START_SCALE, <0.1, 0.1, 0.0>,
            PSYS_PART_END_SCALE, <1.0, 1.0, 0.0>,
            PSYS_SRC_BURST_RATE, 0.1,
            PSYS_SRC_BURST_PART_COUNT, 10,
            PSYS_SRC_BURST_RADIUS, 0.5,
            PSYS_SRC_BURST_SPEED_MIN, 1.0,
            PSYS_SRC_BURST_SPEED_MAX, 3.0,
            PSYS_SRC_ACCEL, <0.0, 0.0, -1.0>, // Gravity
            PSYS_SRC_OMEGA, <0.0, 0.0, 0.0>,
            PSYS_SRC_MAX_AGE, 0.0,
            PSYS_SRC_TEXTURE, "",
            PSYS_SRC_ANGLE_BEGIN, 0.0,
            PSYS_SRC_ANGLE_END, 0.0
        ]);
    }
}
