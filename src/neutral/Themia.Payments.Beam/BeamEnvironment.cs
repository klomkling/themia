namespace Themia.Payments.Beam;

/// <summary>Which Beam environment to call.</summary>
public enum BeamEnvironment
{
    /// <summary>The sandbox. Same API, no real money.</summary>
    Playground,

    /// <summary>Live.</summary>
    Production,
}
