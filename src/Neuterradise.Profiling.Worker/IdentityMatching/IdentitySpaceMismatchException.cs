namespace Neuterradise.Profiling.Worker.IdentityMatching;

public sealed class IdentitySpaceMismatchException : InvalidOperationException
{
    public IdentitySpaceMismatchException(string message) : base(message)
    {
    }

    public IdentitySpaceMismatchException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
