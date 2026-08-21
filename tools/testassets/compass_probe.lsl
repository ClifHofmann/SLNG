default
{
    state_entry()
    {
        // Wir nutzen einfach einen Prisma/Kegel über PRIM_TYPE_PRISM
        llSetPrimitiveParams([
            PRIM_TYPE, PRIM_TYPE_PRISM, 0, <0.0, 1.0, 0.0>, 0.0, <0.0, 0.0, 0.0>, <1.0, 1.0, 0.0>, <0.0, 0.0, 0.0>,
            PRIM_SIZE, <2.0, 2.0, 4.0>,
            PRIM_ROTATION, llEuler2Rot(<-90.0, 0.0, 0.0> * DEG_TO_RAD),
            PRIM_COLOR, ALL_SIDES, <1.0, 0.0, 0.0>, 1.0
        ]);
        
        // Zeigt einen schwebenden Text über dem Prim
        llSetText("--> NORDEN (+Y) -->", <1.0, 1.0, 1.0>, 1.0);
    }
}
