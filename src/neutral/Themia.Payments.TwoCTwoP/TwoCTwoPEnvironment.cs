namespace Themia.Payments.TwoCTwoP;

/// <summary>Which 2C2P PGW environment to call.</summary>
public enum TwoCTwoPEnvironment
{
    /// <summary>The sandbox. Same API, no real money.</summary>
    Sandbox,

    /// <summary>Live.</summary>
    Production,
}
