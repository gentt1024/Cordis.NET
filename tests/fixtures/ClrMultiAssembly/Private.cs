namespace IndependentBundle.Dependency;

public static class Marker
{
    public static string Value() =>
#if SECOND
        "private-2";
#else
        "private-1";
#endif
}
