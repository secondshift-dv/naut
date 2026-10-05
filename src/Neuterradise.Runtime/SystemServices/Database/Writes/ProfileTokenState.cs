namespace Neuterradise.App.SystemServices.Database.Writes;

internal sealed record ProfileTokenState(string? Token, long RowVersion);
