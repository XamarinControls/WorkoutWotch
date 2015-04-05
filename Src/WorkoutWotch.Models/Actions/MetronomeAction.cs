﻿namespace WorkoutWotch.Models.Actions
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Threading.Tasks;
    using Kent.Boogaart.HelperTrinity.Extensions;
    using WorkoutWotch.Services.Contracts.Audio;
    using WorkoutWotch.Services.Contracts.Delay;
    using WorkoutWotch.Services.Contracts.Logger;

    public sealed class MetronomeAction : IAction
    {
        private readonly IImmutableList<MetronomeTick> ticks;
        private readonly SequenceAction innerAction;

        public MetronomeAction(IAudioService audioService, IDelayService delayService, ILoggerService loggerService, IEnumerable<MetronomeTick> ticks)
        {
            audioService.AssertNotNull(nameof(audioService));
            delayService.AssertNotNull(nameof(delayService));
            loggerService.AssertNotNull(nameof(loggerService));
            ticks.AssertNotNull(nameof(ticks));

            this.ticks = ticks.ToImmutableList();
            this.innerAction = new SequenceAction(GetInnerActions(audioService, delayService, loggerService, this.ticks));
        }

        public TimeSpan Duration => this.innerAction.Duration;

        public IImmutableList<MetronomeTick> Ticks => this.ticks;

        public async Task ExecuteAsync(ExecutionContext context)
        {
            context.AssertNotNull(nameof(context));

            await this
                .innerAction
                .ExecuteAsync(context)
                .ContinueOnAnyContext();
        }

        private static IEnumerable<IAction> GetInnerActions(IAudioService audioService, IDelayService delayService, ILoggerService loggerService, IEnumerable<MetronomeTick> ticks)
        {
            foreach (var tick in ticks)
            {
                yield return new WaitAction(delayService, tick.PeriodBefore);

                switch (tick.Type)
                {
                    case MetronomeTickType.Click:
                        yield return new DoNotAwaitAction(loggerService, new AudioAction(audioService, "Audio/MetronomeClick.mp3"));
                        break;
                    case MetronomeTickType.Bell:
                        yield return new DoNotAwaitAction(loggerService, new AudioAction(audioService, "Audio/MetronomeBell.mp3"));
                        break;
                }
            }
        }
    }
}