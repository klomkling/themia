namespace Themia.ETax.Withholding;

/// <summary>Why a render was refused because of the signature.</summary>
public enum WithholdingSignatureFailure
{
    /// <summary>The certificate was issued with a signature image and this render supplied a different one.</summary>
    DifferentImage = 0,

    /// <summary>The certificate was issued with a signature image and this render supplied none, so it would be printed unsigned.</summary>
    MissingImage = 1,

    /// <summary>The certificate was issued without a signature and this render supplied one: an old certificate is not re-rendered with a signature it never had.</summary>
    NotIssuedWithOne = 2,
}

/// <summary>
/// A stored certificate was not rendered because the signature image does not match the one recorded when it was issued. Nothing is printed: a
/// certificate is never silently re-rendered with another signer's signature or without its own. The message names no certificate content.
/// </summary>
public sealed class WithholdingSignatureException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">What did not match.</param>
    public WithholdingSignatureException(WithholdingSignatureFailure failure)
        : base($"The certificate was not rendered: the signature image does not match the one it was issued with ({failure}). Supply the image recorded at issue.")
    {
        Failure = failure;
    }

    /// <summary>What did not match.</summary>
    public WithholdingSignatureFailure Failure { get; }
}
