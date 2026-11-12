namespace SomeDataProvider.DtcProtocolServer.Test;

using System.Context;
using NUnit.Framework;

/// <summary>Use the same async-local context resolver as the CLI host, not NBLib's global default resolver.</summary>
[SetUpFixture]
public sealed class ServerTestContext
{
	IContextResolver? _previous;

	[OneTimeSetUp]
	public void Start()
	{
		_previous = GetContext.Current;
		GetContext.Current = new ContextAsyncLocalResolver { Value = GetContext.Root };
	}

	[OneTimeTearDown]
	public void Stop() => GetContext.Current = _previous!;
}
