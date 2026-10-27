namespace Koleksi.Infrastructure;

/// <summary>Product features that are built but not yet switched on. Every one is off unless a stage turns it on.</summary>
public sealed class FeaturesOptions
{
    /// <summary>
    /// Identity-document upload and the ID-verification submission. Off by default: nobody reviews the documents yet, so
    /// accepting a person's ID photo would be storing sensitive data with nothing to do with it (QA P-07). The web app's
    /// own switch (<c>FEATURE_ID_VERIFICATION</c>) only hides the form; this is what actually refuses the request.
    /// </summary>
    /// <remarks>Settable so a test can flip it for one class of tests; configuration binds it once at startup.</remarks>
    public bool IdVerificationEnabled { get; set; }
}
