namespace SomeDataProvider.DtcProtocolServer.Market;

using System;

readonly record struct CoverageInterval(DateTimeOffset Start, DateTimeOffset End);
