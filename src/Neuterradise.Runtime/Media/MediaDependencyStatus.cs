namespace Neuterradise.App.Media;

/// <summary>
/// Canonical persisted completeness of an asset package (the product contract, <c>media.dependency_status</c>):
/// <c>SELF_CONTAINED | COMPLETE | DEPENDENCIES_MISSING | DEPENDENCIES_UNKNOWN</c>.
/// A single-file image or video is self contained; a model package with all of its component files
/// present is complete. Missing and unknown are different truths and never collapse into one.
/// </summary>
public enum MediaDependencyStatus
{
    /// <summary>The asset is one file and has no component dependencies.</summary>
    SelfContained,

    /// <summary>Every declared component of the package is present and accounted for.</summary>
    Complete,

    /// <summary>At least one declared component is known to be absent.</summary>
    DependenciesMissing,

    /// <summary>The package declares components that have not been resolved yet.</summary>
    DependenciesUnknown,
}
