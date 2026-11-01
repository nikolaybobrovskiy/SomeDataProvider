namespace SomeDataProvider.DataStorage.TInvest.Test;

using NUnit.Framework;
using SomeDataProvider.DtcProtocolServer.Test;

[TestFixture]
public class RateLimiterSimulationTests
{
	const string LongRunCategory = "TInvestLimiterSimulation";

	[Test]
	public void FixedSeedsKeepEveryInvariant()
	{
		var total = new LimiterSimulation.Outcome();
		for (var seed = 1; seed <= 16; seed++) total.Add(new LimiterSimulation(LimiterSimulation.RandomScenario(seed, TimeSpan.FromMinutes(2)), seed).Run());
		TestContext.Out.WriteLine(total);
		Assert.That(total.Violations, Is.Empty, string.Join("\n", total.Violations));
		Assert.That(total.Plans, Is.GreaterThan(100), "The fixed seeds must exercise many plans.");
		Assert.That(total.Lookups, Is.GreaterThan(500), "The fixed seeds must exercise many lookups.");
	}

	[Test]
	[Explicit("Long local limiter simulation; run on demand, never by a default or CI run.")]
	[Category(LongRunCategory)]
	public void LongRunKeepsEveryInvariant()
	{
		var total = new LimiterSimulation.Outcome();
		for (var seed = 1; seed <= 200; seed++) total.Add(new LimiterSimulation(LimiterSimulation.RandomScenario(seed, TimeSpan.FromMinutes(10)), seed).Run());
		TestContext.Out.WriteLine(total);
		Assert.That(total.Violations, Is.Empty, string.Join("\n", total.Violations));
	}

	[TestCase(400)]
	[TestCase(120)]
	public void SequentialPlanLeavesEveryLookupItsFiveSlots(int calls)
	{
		var result = new LimiterSimulation(new LimiterSimulation.Scenario
		{
			Duration = TimeSpan.FromSeconds(40),
			Plans = new[] { (TimeSpan.Zero, calls) },
			MaximumLatency = TimeSpan.FromMilliseconds(100),
			Jitter = false,
			MinimumLookupGap = TimeSpan.FromSeconds(1),
			MaximumLookupGap = TimeSpan.FromSeconds(1),
		}, calls).Run();
		TestContext.Out.WriteLine(result);
		Assert.That(result.Violations, Is.Empty, string.Join("\n", result.Violations));
		Assert.That(result.PlansCompleted, Is.EqualTo(1));
		Assert.That(result.LookupsFailed, Is.Zero);
		Assert.That(result.LookupsAtOnce, Is.EqualTo(result.Lookups), "Every lookup is admitted at once from the five free slots.");
	}

	[Test]
	public void TwoPlansAtOnceKeepPacingWithoutMultiSecondGaps()
	{
		var result = new LimiterSimulation(new LimiterSimulation.Scenario
		{
			Duration = TimeSpan.FromSeconds(60),
			Plans = new[] { (TimeSpan.Zero, 200), (TimeSpan.Zero, 200) },
			MaximumLatency = TimeSpan.FromMilliseconds(100),
			Jitter = false,
		}, 200).Run();
		TestContext.Out.WriteLine(result);
		Assert.That(result.Violations, Is.Empty, string.Join("\n", result.Violations));
		Assert.That(result.PlansCompleted, Is.EqualTo(2));
		Assert.That(result.MaxPlanGap, Is.LessThan(LimiterSimulation.Second * 3 / 2), "Neither plan stalls for seconds behind the other's bookings.");
	}
}

/// <summary>
/// A seeded simulation of the shared limiter on a fake clock. History plans, lookups, header corrections,
/// quota exhaustion, source failures and cancelled plans use one limiter, and the invariants are checked after
/// every step. A blocked history call is retried when its binding limit frees, or every 5 ms, instead of waiting on
/// a timer, so a run stays deterministic for its seed.
/// </summary>
sealed class LimiterSimulation
{
	public const long Second = TimeSpan.TicksPerSecond;
	const long Millisecond = TimeSpan.TicksPerMillisecond;
	const long Poll = 5 * Millisecond;
	const int HistoryPerSecond = 40;
	const int AddressPerSecond = 45;
	const int MarketDataPerMinute = 540;
	const int InstrumentsPerMinute = 180;
	const int MaximumCandleCalls = 400;
	readonly ManualTimeProvider _clock = new();
	readonly TInvestRateLimiter _limiter;
	readonly Random _random;
	readonly Scenario _scenario;
	readonly List<Admission> _calls = new();
	readonly List<Plan> _plans = new();
	readonly List<Lookup> _lookups = new();
	readonly Queue<(long At, int Calls)> _fixedPlans = new();
	readonly long _end;
	int _capacity = MarketDataPerMinute;
	long _blockedUntil;
	long _nextArrival;
	long _nextLookup;
	int _ids;

	public LimiterSimulation(Scenario scenario, int seed)
	{
		_scenario = scenario;
		_random = new Random(seed);
		_limiter = new TInvestRateLimiter(_clock);
		_end = scenario.Duration.Ticks;
		Result = new Outcome { Seed = seed, Scenarios = 1 };
		if (scenario.Plans != null)
		{
			foreach (var (at, calls) in scenario.Plans.OrderBy(p => p.At)) _fixedPlans.Enqueue((at.Ticks, calls));
			_nextArrival = _fixedPlans.Count > 0 ? _fixedPlans.Peek().At : long.MaxValue;
		}
		_nextLookup = scenario.FirstLookup.Ticks;
	}

	public Outcome Result { get; }

	long Now => _clock.GetTimestamp();

	public static Scenario RandomScenario(int seed, TimeSpan duration)
	{
		var random = new Random(seed ^ 0x5bd1e995);
		return new Scenario
		{
			Duration = duration,
			MaximumPlansAtOnce = random.Next(1, 4),
			ExhaustedChance = 0.003,
			LowRemainingChance = 0.002,
			LowLimitChance = 0.0003,
			FailureChance = 0.002,
			CancelChance = 0.002,
		};
	}

	public Outcome Run()
	{
		var started = System.Diagnostics.Stopwatch.StartNew();
		while (true)
		{
			var next = Math.Min(Math.Min(_nextArrival, _nextLookup), Math.Min(_plans.Select(p => p.NextAt).DefaultIfEmpty(long.MaxValue).Min(),
				_lookups.Select(l => l.NextAt).DefaultIfEmpty(long.MaxValue).Min()));
			if (next >= _end) break;
			if (next > Now) _clock.Advance(TimeSpan.FromTicks(next - Now));
			if (_nextArrival == Now)
			{
				Arrive();
				Check();
			}
			foreach (var plan in _plans.Where(p => p.NextAt == Now).ToArray())
			{
				if (plan.InFlight) Respond(plan);
				else Attempt(plan);
				Check();
			}
			if (_nextLookup == Now)
			{
				_lookups.Add(new Lookup { Start = Now, NextAt = Now });
				_nextLookup = Now + Uniform(_scenario.MinimumLookupGap.Ticks, _scenario.MaximumLookupGap.Ticks);
			}
			foreach (var lookup in _lookups.Where(l => l.NextAt == Now).ToArray())
			{
				TryLookup(lookup);
				Check();
			}
		}
		foreach (var plan in _plans.ToArray()) End(plan, () => Result.PlansUnfinished++);
		Check();
		Result.SimulatedTicks = _end;
		Result.Elapsed = started.Elapsed;
		return Result;
	}

	static DateTimeOffset Clock(long ticks) => DateTimeOffset.UnixEpoch.AddTicks(ticks);

	void Arrive()
	{
		int calls;
		if (_scenario.Plans != null)
		{
			calls = _fixedPlans.Dequeue().Calls;
			_nextArrival = _fixedPlans.Count > 0 ? _fixedPlans.Peek().At : long.MaxValue;
		}
		else
		{
			_nextArrival = Now + Uniform(0, 6 * Second);
			if (_plans.Count >= _scenario.MaximumPlansAtOnce) return;
			calls = _random.Next(4) switch
			{
				0 => _random.Next(1, 11),
				1 => _random.Next(11, 81),
				2 => _random.Next(81, 201),
				_ => _random.Next(201, MaximumCandleCalls + 1),
			};
		}
		TInvestRateLimiter.Reservation reservation;
		var deadline = Now + (20 * Second);
		var horizon = EarliestLastCall(_plans.Sum(p => p.Calls - p.Consumed), calls);
		try
		{
			reservation = _limiter.ReserveAsync(TInvestLimitGroup.MarketData, calls, Clock(deadline), default).GetAwaiter().GetResult();
		}
		catch (TInvestRequestException ex) when (ex.Kind == TInvestFailureKind.Limit)
		{
			Result.PlansRejected++;
			if (_plans.Count == 0 && Now >= _blockedUntil && Count(TInvestLimitGroup.MarketData, 60 * Second) + calls <= _capacity)
				Fail($"a {calls}-call plan was rejected though no plan was active and quota allowed it");
			return;
		}
		if (horizon >= deadline)
		{
			Fail($"a {calls}-call plan was admitted though its last call cannot go before {TimeSpan.FromTicks(horizon).TotalSeconds:0.#######} s, after its deadline "
				+ $"(blocked until {TimeSpan.FromTicks(_blockedUntil).TotalSeconds:0.#######} s, used {Count(TInvestLimitGroup.MarketData, 60 * Second)} of {_capacity}, "
				+ $"booked {_plans.Sum(p => p.Calls - p.Consumed)})");
		}
		Result.Plans++;
		var latency = _scenario.MaximumLatency.Ticks;
		_plans.Add(new Plan
		{
			Id = ++_ids,
			Calls = calls,
			Reservation = reservation,
			NextAt = Now,
			BaseLatency = _scenario.Jitter ? Uniform(0, latency * 3 / 4) : 0,
		});
	}

	void Attempt(Plan plan)
	{
		if (!plan.Waiting)
		{
			if (Chance(_scenario.CancelChance))
			{
				End(plan, () => Result.PlansCancelled++);
				return;
			}
			plan.Waiting = true;
			plan.RequestedAt = Now;
			plan.Deadline = Now + (20 * Second);
		}
		using var cancel = new CancellationTokenSource();
		var task = plan.Retry
			? plan.Reservation.AcquireRetryAsync(_limiter, MaximumCandleCalls, Clock(plan.Deadline), cancel.Token)
			: plan.Reservation.AcquireAsync(_limiter, Clock(plan.Deadline), cancel.Token);
		if (task.IsCompletedSuccessfully)
		{
			Admit(plan);
			return;
		}
		if (task.IsCompleted)
		{
			if (task.Exception?.InnerException is not TInvestRequestException { Kind: TInvestFailureKind.Limit })
			{
				Fail($"plan {plan.Id} call failed unexpectedly: {task.Exception?.InnerException?.GetType().Name}");
			}
			else if (!SourceBlocked(plan))
			{
				Fail($"plan {plan.Id} missed its 20 s deadline without a source block (deadline {TimeSpan.FromTicks(plan.Deadline).TotalSeconds:0.#######} s, "
					+ $"blocked until {TimeSpan.FromTicks(_blockedUntil).TotalSeconds:0.#######} s, used {Count(TInvestLimitGroup.MarketData, 60 * Second)}, "
					+ $"booked by others {_plans.Where(p => p != plan).Sum(p => p.Calls - p.Consumed)}, retry {plan.Retry})");
			}
			End(plan, () => Result.PlansFailedDeadline++);
			return;
		}
		cancel.Cancel();
		try
		{
			task.GetAwaiter().GetResult();
		}
		catch (OperationCanceledException)
		{
		}
		Result.HistoryDenials++;
		if (Count(null, Second, history: true) < HistoryPerSecond && Count(null, Second) < AddressPerSecond && !SourceBlocked(plan))
			Fail($"plan {plan.Id} call {plan.Consumed + 1} waited on booked slots, not on actual calls");

		// A real waiter wakes when the binding limit frees; polling only every 5 ms could overshoot its deadline.
		var free = ExpectedFree();
		plan.NextAt = free > Now ? Math.Min(Now + Poll, free) : Now + Poll;
	}

	void Admit(Plan plan)
	{
		Record(TInvestLimitGroup.MarketData, true);
		Result.HistoryCalls++;
		if (plan.Retry) Result.Retries++;
		else plan.Consumed++;
		Result.MaxHistoryWait = Math.Max(Result.MaxHistoryWait, Now - plan.RequestedAt);
		if (plan.LastAdmission >= 0) plan.MaxGap = Math.Max(plan.MaxGap, Now - plan.LastAdmission);
		plan.LastAdmission = Now;
		plan.Waiting = false;
		plan.InFlight = true;
		var maximum = _scenario.MaximumLatency.Ticks;
		var jitter = _scenario.Jitter ? Uniform(0, maximum / 4) : Uniform(0, maximum);
		plan.NextAt = Now + Math.Min(maximum, plan.BaseLatency + jitter);
	}

	void Respond(Plan plan)
	{
		plan.InFlight = false;
		var draw = _random.NextDouble();
		if (draw < _scenario.ExhaustedChance)
		{
			var reset = _random.Next(1, 61) * Second;
			_limiter.Correct(TInvestLimitGroup.MarketData, null, null, TimeSpan.FromTicks(reset), exhausted: true);
			_blockedUntil = Math.Max(_blockedUntil, Now + reset);
			Result.Exhausted++;
			if (!plan.Retry && Now < plan.Deadline)
			{
				plan.Retry = true;
				plan.Waiting = true;
				plan.NextAt = Now;
				return;
			}
			End(plan, () => Result.PlansFailedSource++);
			return;
		}
		if (draw < _scenario.ExhaustedChance + _scenario.FailureChance)
		{
			End(plan, () => Result.PlansFailedSource++);
			return;
		}
		var used = Count(TInvestLimitGroup.MarketData, 60 * Second);
		if (Chance(_scenario.LowRemainingChance))
		{
			var reset = _random.Next(1, 61) * Second;
			var remaining = Math.Max(0, _capacity - used - _random.Next(1, 21));
			_limiter.Correct(TInvestLimitGroup.MarketData, 600, remaining, TimeSpan.FromTicks(reset));

			// A header blocks until its reset only when it reports fewer calls than the window has left; with the window
			// already full (also after a lower limit), the limiter waits for its own window instead.
			if (remaining < _capacity - used) _blockedUntil = Math.Max(_blockedUntil, Now + reset);
			Result.LowRemaining++;
		}
		else if (Chance(_scenario.LowLimitChance))
		{
			// A lower tariff limit shrinks the window for the rest of the run; the remaining count matches it.
			var limit = _random.Next(300, 600);
			_limiter.Correct(TInvestLimitGroup.MarketData, limit, limit - used, TimeSpan.FromSeconds(_random.Next(1, 61)));
			_capacity = Math.Min(_capacity, Math.Max(1, (int)(limit * 0.9)));
			Result.LowLimits++;
		}
		else
		{
			_limiter.Correct(TInvestLimitGroup.MarketData, 600, 600 - used, TimeSpan.FromSeconds(_random.Next(1, 61)));
		}
		plan.Retry = false;
		if (++plan.Completed == plan.Calls)
		{
			End(plan, () => Result.PlansCompleted++);
			return;
		}
		plan.NextAt = Now + Uniform(0, 5 * Millisecond);
	}

	void TryLookup(Lookup lookup)
	{
		var delay = _limiter.TryAcquire(TInvestLimitGroup.Instruments);
		if (delay == TimeSpan.Zero)
		{
			Record(TInvestLimitGroup.Instruments, false);
			Result.Lookups++;
			var wait = Now - lookup.Start;
			if (wait == 0) Result.LookupsAtOnce++;
			Result.MaxLookupWait = Math.Max(Result.MaxLookupWait, wait);
			if (wait > Second) Fail($"a lookup waited {TimeSpan.FromTicks(wait).TotalSeconds:0.###} s");
			_lookups.Remove(lookup);
			return;
		}
		if (ControlSlotsFree()) Fail($"a lookup was denied for {delay.TotalSeconds:0.###} s while five control slots were free");
		if (Now + delay.Ticks >= lookup.Start + (8 * Second))
		{
			Result.LookupsFailed++;
			Fail("a lookup failed its 8 s budget");
			_lookups.Remove(lookup);
			return;
		}
		lookup.NextAt = Now + delay.Ticks;
	}

	void End(Plan plan, Action count)
	{
		count();
		plan.Reservation.Dispose();
		_plans.Remove(plan);
		Result.MaxPlanGap = Math.Max(Result.MaxPlanGap, plan.MaxGap);
	}

	void Check()
	{
		Result.Steps++;
		var history = Count(null, Second, history: true);
		var address = Count(null, Second);
		if (history > HistoryPerSecond) Fail($"{history} history calls in one rolling second");
		if (address > AddressPerSecond) Fail($"{address} calls from the address in one rolling second");
		if (Count(TInvestLimitGroup.MarketData, 60 * Second) > MarketDataPerMinute) Fail("market-data service window exceeded");
		if (Count(TInvestLimitGroup.Instruments, 60 * Second) > InstrumentsPerMinute) Fail("instruments service window exceeded");
		var probe = _limiter.Probe(TInvestLimitGroup.Instruments);
		if (probe != TimeSpan.Zero && ControlSlotsFree())
			Fail($"others see no free control slot ({probe.TotalSeconds:0.#######} s) though fewer than five went out");
		if (_plans.Count == 0 && address < AddressPerSecond && Now >= _blockedUntil
			&& Count(TInvestLimitGroup.MarketData, 60 * Second) < _capacity && _limiter.Probe(TInvestLimitGroup.MarketData) != TimeSpan.Zero)
			Fail("an ended plan left slots behind");
	}

	bool ControlSlotsFree() => Count(null, Second, history: false) < AddressPerSecond - HistoryPerSecond && Count(null, Second) < AddressPerSecond
		&& Count(TInvestLimitGroup.Instruments, 60 * Second) < InstrumentsPerMinute;

	/// <summary>
	/// True when a quota block or the service window can hold back <paramref name="plan"/>'s call: the window holds
	/// every actual call and the other plans' booked calls, never the plan's own.
	/// </summary>
	bool SourceBlocked(Plan plan) => Now < _blockedUntil
		|| Count(TInvestLimitGroup.MarketData, 60 * Second) + _plans.Where(p => p != plan).Sum(p => p.Calls - p.Consumed) >= _capacity;

	/// <summary>
	/// A lower bound on when the last of <paramref name="calls"/> new booked calls can go, queued behind
	/// <paramref name="booked"/> calls already booked: the quota block, 40 history and 45 calls per rolling second, and
	/// the market-data window, from the actual calls so far. A plan admitted past its deadline breaks A-5.
	/// </summary>
	long EarliestLastCall(int booked, int calls)
	{
		if (calls == 0) return Now;
		var history = Times(Second, history: true);
		var address = Times(Second);
		var service = Times(60 * Second, group: TInvestLimitGroup.MarketData);
		var at = Math.Max(Now, _blockedUntil);
		for (var i = 0; i < booked + calls; i++)
		{
			if (history.Count >= HistoryPerSecond) at = Math.Max(at, history[^HistoryPerSecond] + Second);
			if (address.Count >= AddressPerSecond) at = Math.Max(at, address[^AddressPerSecond] + Second);
			if (service.Count >= _capacity) at = Math.Max(at, service[^_capacity] + (60 * Second));
			history.Add(at);
			address.Add(at);
			service.Add(at);
		}
		return at;
	}

	long ExpectedFree()
	{
		var at = Math.Max(Now, _blockedUntil);
		var history = Times(Second, history: true);
		if (history.Count >= HistoryPerSecond) at = Math.Max(at, history[^HistoryPerSecond] + Second);
		var address = Times(Second);
		if (address.Count >= AddressPerSecond) at = Math.Max(at, address[^AddressPerSecond] + Second);
		return at;
	}

	List<long> Times(long window, bool? history = null, TInvestLimitGroup? group = null)
	{
		var times = new List<long>();
		for (var i = _calls.Count - 1; i >= 0 && _calls[i].At > Now - window; i--)
		{
			if ((history == null || _calls[i].History == history) && (group == null || _calls[i].Group == group)) times.Insert(0, _calls[i].At);
		}
		return times;
	}

	void Record(TInvestLimitGroup group, bool history)
	{
		// A lower limit header cannot undo calls already sent, so the lowered window is checked on admission.
		if (group == TInvestLimitGroup.MarketData && Count(group, 60 * Second) >= _capacity)
			Fail($"a market-data call was admitted with the lowered window of {_capacity} full");
		_calls.Add(new Admission(Now, group, history));
	}

	int Count(TInvestLimitGroup? group, long window, bool? history = null)
	{
		var count = 0;
		for (var i = _calls.Count - 1; i >= 0 && _calls[i].At > Now - window; i--)
		{
			var call = _calls[i];
			if ((group == null || call.Group == group) && (history == null || call.History == history)) count++;
		}
		return count;
	}

	void Fail(string message)
	{
		Result.ViolationCount++;
		if (Result.Violations.Count < 20) Result.Violations.Add($"seed {Result.Seed} at {TimeSpan.FromTicks(Now).TotalSeconds:0.#######} s: {message}");
	}

	bool Chance(double probability) => probability > 0 && _random.NextDouble() < probability;

	long Uniform(long minimum, long maximum) => maximum <= minimum ? minimum : minimum + _random.NextInt64(maximum - minimum);

	readonly record struct Admission(long At, TInvestLimitGroup Group, bool History);

	public sealed record Scenario
	{
		public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(2);

		/// <summary>Fixed plan arrivals; null draws random plans of 1 to 400 calls.</summary>
		public IReadOnlyList<(TimeSpan At, int Calls)>? Plans { get; init; }

		public int MaximumPlansAtOnce { get; init; } = 3;

		public TimeSpan MaximumLatency { get; init; } = TimeSpan.FromMilliseconds(200);

		/// <summary>True draws a base latency per plan plus per-call jitter; false draws each latency uniformly.</summary>
		public bool Jitter { get; init; } = true;

		public TimeSpan FirstLookup { get; init; } = TimeSpan.FromMilliseconds(500);

		public TimeSpan MinimumLookupGap { get; init; } = TimeSpan.FromMilliseconds(500);

		public TimeSpan MaximumLookupGap { get; init; } = TimeSpan.FromSeconds(2);

		public double ExhaustedChance { get; init; }

		public double LowRemainingChance { get; init; }

		/// <summary>Chance per answer of a header with a lower limit of 300 to 599 calls a minute.</summary>
		public double LowLimitChance { get; init; }

		public double FailureChance { get; init; }

		public double CancelChance { get; init; }
	}

	public sealed class Outcome
	{
		public int Seed { get; set; }
		public int Scenarios { get; set; }
		public long Steps { get; set; }
		public long SimulatedTicks { get; set; }
		public TimeSpan Elapsed { get; set; }
		public int Plans { get; set; }
		public int PlansRejected { get; set; }
		public int PlansCompleted { get; set; }
		public int PlansCancelled { get; set; }
		public int PlansFailedSource { get; set; }
		public int PlansFailedDeadline { get; set; }
		public int PlansUnfinished { get; set; }
		public long HistoryCalls { get; set; }
		public long HistoryDenials { get; set; }
		public long Retries { get; set; }
		public long Exhausted { get; set; }
		public long LowRemaining { get; set; }
		public long LowLimits { get; set; }
		public long Lookups { get; set; }
		public long LookupsAtOnce { get; set; }
		public long LookupsFailed { get; set; }
		public long MaxLookupWait { get; set; }
		public long MaxHistoryWait { get; set; }
		public long MaxPlanGap { get; set; }
		public long ViolationCount { get; set; }
		public List<string> Violations { get; } = new();
		public List<int> FailingSeeds { get; } = new();

		public void Add(Outcome other)
		{
			Scenarios += other.Scenarios;
			Steps += other.Steps;
			SimulatedTicks += other.SimulatedTicks;
			Elapsed += other.Elapsed;
			Plans += other.Plans;
			PlansRejected += other.PlansRejected;
			PlansCompleted += other.PlansCompleted;
			PlansCancelled += other.PlansCancelled;
			PlansFailedSource += other.PlansFailedSource;
			PlansFailedDeadline += other.PlansFailedDeadline;
			PlansUnfinished += other.PlansUnfinished;
			HistoryCalls += other.HistoryCalls;
			HistoryDenials += other.HistoryDenials;
			Retries += other.Retries;
			Exhausted += other.Exhausted;
			LowRemaining += other.LowRemaining;
			LowLimits += other.LowLimits;
			Lookups += other.Lookups;
			LookupsAtOnce += other.LookupsAtOnce;
			LookupsFailed += other.LookupsFailed;
			MaxLookupWait = Math.Max(MaxLookupWait, other.MaxLookupWait);
			MaxHistoryWait = Math.Max(MaxHistoryWait, other.MaxHistoryWait);
			MaxPlanGap = Math.Max(MaxPlanGap, other.MaxPlanGap);
			ViolationCount += other.ViolationCount;
			if (other.ViolationCount > 0) FailingSeeds.Add(other.Seed);
			foreach (var violation in other.Violations.Take(Math.Max(0, 20 - Violations.Count))) Violations.Add(violation);
		}

		public override string ToString() =>
			$"scenarios {Scenarios}, steps {Steps}, simulated {TimeSpan.FromTicks(SimulatedTicks).TotalMinutes:0.#} min, elapsed {Elapsed.TotalSeconds:0.#} s; "
			+ $"plans {Plans} (rejected {PlansRejected}, completed {PlansCompleted}, cancelled {PlansCancelled}, source failures {PlansFailedSource}, "
			+ $"deadline failures {PlansFailedDeadline}, unfinished {PlansUnfinished}); history calls {HistoryCalls} (retries {Retries}, polls denied {HistoryDenials}), "
			+ $"exhausted {Exhausted}, low remaining headers {LowRemaining}, lower limit headers {LowLimits}; lookups {Lookups} (at once {LookupsAtOnce}, failed {LookupsFailed}), "
			+ $"max lookup wait {TimeSpan.FromTicks(MaxLookupWait).TotalSeconds:0.###} s, max history wait {TimeSpan.FromTicks(MaxHistoryWait).TotalSeconds:0.###} s, "
			+ $"max plan gap {TimeSpan.FromTicks(MaxPlanGap).TotalSeconds:0.###} s; violations {ViolationCount}"
			+ (FailingSeeds.Count > 0 ? $" in seeds {string.Join(", ", FailingSeeds)}" : string.Empty);
	}

	sealed class Plan
	{
		public int Id { get; init; }
		public int Calls { get; init; }
		public TInvestRateLimiter.Reservation Reservation { get; init; } = null!;
		public long BaseLatency { get; init; }
		public long NextAt { get; set; }
		public bool InFlight { get; set; }
		public bool Waiting { get; set; }
		public bool Retry { get; set; }
		public int Consumed { get; set; }
		public int Completed { get; set; }
		public long RequestedAt { get; set; }
		public long Deadline { get; set; }
		public long LastAdmission { get; set; } = -1;
		public long MaxGap { get; set; }
	}

	sealed class Lookup
	{
		public long Start { get; init; }
		public long NextAt { get; set; }
	}
}
