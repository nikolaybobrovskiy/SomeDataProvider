namespace SomeDataProvider.DtcProtocolServer.Test;

using System;
using System.Buffers.Binary;
using System.Text;
using Dtc.Protocol.Private;
using NUnit.Framework;
using SomeDataProvider.DataStorage.TInvest.Test;

public partial class ProviderInformationWireTests
{
	[Test]
	public void PinnedCodecRejectsUnknownHelloModeBeforeConsumingNegotiation()
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		var bytes = ProviderFrame(new ProviderHelloRequest { BudgetScopeID = "synthetic-profile", Source = "t-invest", InstrumentUID = "synthetic-uid", UIDEpoch = "synthetic-epoch" });
		BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), ProviderHelloModes.ClockOnly);
		Assert.That(((ProviderHelloRequest)ProviderInformationCodec.DecodeVls(bytes)).Mode, Is.EqualTo(ProviderHelloModes.ClockOnly));
		client.GetStream().Write(bytes);
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo("FeatureUnavailable"));
		BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 3);
		client.GetStream().Write(bytes);
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo("InvalidRequest"));
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-profile" });
		Assert.That(Read(client), Is.TypeOf<ProviderHelloResponse>());
		Assert.That(gateway.Lookups, Is.Empty);
	}

	[TestCase(0)]
	[TestCase(3)]
	[TestCase(257)]
	public void PinnedCodecRejectsBlankAndOversizedScopeBeforeNegotiation(int scopeBytes)
	{
		using var gateway = new ScriptedTInvestGateway();
		using var host = new TInvestTestHost(gateway, new ManualTimeProvider());
		using var client = Connect(host);
		var valid = ProviderFrame(new ProviderHelloRequest { BudgetScopeID = "synthetic-profile" });
		var control = ReplaceHelloScope(valid, "synthetic-control");
		Assert.That(((ProviderHelloRequest)ProviderInformationCodec.DecodeVls(control)).BudgetScopeID, Is.EqualTo("synthetic-control"));
		var frame = ReplaceHelloScope(valid, new string(scopeBytes == 3 ? ' ' : 'x', scopeBytes));
		client.GetStream().Write(frame);
		Assert.That(((ProviderRequestReject)Read(client)).ReasonCode, Is.EqualTo("InvalidRequest"));
		Send(client, new ProviderHelloRequest { BudgetScopeID = "synthetic-profile" });
		Assert.That(Read(client), Is.TypeOf<ProviderHelloResponse>());
		Assert.That(gateway.Lookups, Is.Empty);
	}

	[Test]
	public void PinnedCodecRejectsAThirdSourceAttemptBeforeBudgetOrSourceWork()
	{
		using var fixture = new RecentWireFixture(1);
		using var history = fixture.Join();
		var bytes = ProviderFrame(fixture.Request);
		BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(76), 2);
		Assert.That(((RecentTickReconcileRequest)ProviderInformationCodec.DecodeVls(bytes)).MaximumSourceAttempts, Is.EqualTo(2));
		BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(76), 3);
		history.GetStream().Write(bytes);
		Assert.That(((ProviderRequestReject)Read(history)).ReasonCode, Is.EqualTo("InvalidRequest"));
		Send(history, fixture.Refresh());
		Assert.That(Read(history), Is.TypeOf<ProviderHelloResponse>());
		Assert.That(fixture.Gateway.LastTradeRequests, Is.Empty);
	}

	static byte[] ReplaceHelloScope(byte[] valid, string scope)
	{
		var baseSize = BinaryPrimitives.ReadUInt16LittleEndian(valid.AsSpan(4));
		var frame = new byte[baseSize + scope.Length + 1];
		valid.AsSpan(0, baseSize).CopyTo(frame);
		BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)frame.Length);
		BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(36), baseSize);
		BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(38), (ushort)(scope.Length + 1));
		Encoding.ASCII.GetBytes(scope).CopyTo(frame, baseSize);
		return frame;
	}

	static byte[] ProviderFrame(ProviderInformationMessage message)
	{
		var bytes = new byte[65535];
		var count = ProviderInformationCodec.EncodeVls(message, bytes);
		return bytes.AsSpan(0, count).ToArray();
	}
}
