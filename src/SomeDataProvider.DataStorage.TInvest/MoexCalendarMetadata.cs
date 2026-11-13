namespace SomeDataProvider.DataStorage.TInvest;

/// <summary>Revision-bound documentary scope, retained internally until a labelled wire contract is approved.</summary>
public sealed record MoexCalendarMetadata(string Revision, DateOnly CheckedFrom, DateOnly CheckedTo,
	DateOnly AnnouncedThrough, DateOnly AnnouncedAsOf);
