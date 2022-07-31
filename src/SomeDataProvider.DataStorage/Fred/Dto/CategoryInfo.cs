namespace SomeDataProvider.DataStorage.Fred.Dto;

using System;

using Newtonsoft.Json;

public sealed class CategoryInfo : IEquatable<CategoryInfo>
{
	public static bool operator ==(CategoryInfo? left, CategoryInfo? right)
	{
		return Equals(left, right);
	}

	public static bool operator !=(CategoryInfo? left, CategoryInfo? right)
	{
		return !Equals(left, right);
	}

	public int Id { get; init; }

	[JsonProperty("parent_id")]
	public int ParentId { get; init; }

	public string Name { get; init; }

	public bool Equals(CategoryInfo? other)
	{
		if (ReferenceEquals(null, other))
			return false;
		if (ReferenceEquals(this, other))
			return true;
		return Id == other.Id;
	}

	public override bool Equals(object? obj)
	{
		return ReferenceEquals(this, obj) || obj is CategoryInfo other && Equals(other);
	}

	public override int GetHashCode()
	{
		return Id;
	}
}