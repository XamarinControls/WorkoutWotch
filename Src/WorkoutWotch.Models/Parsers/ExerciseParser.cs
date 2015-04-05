﻿namespace WorkoutWotch.Models.Parsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reactive;
    using Kent.Boogaart.HelperTrinity.Extensions;
    using Sprache;
    using WorkoutWotch.Models.Actions;
    using WorkoutWotch.Models.EventMatchers;
    using WorkoutWotch.Models.Events;
    using WorkoutWotch.Services.Contracts.Container;
    using WorkoutWotch.Services.Contracts.Logger;
    using WorkoutWotch.Services.Contracts.Speech;

    internal static class ExerciseParser
    {
        // parses the set and rep count. e.g. "3 sets x 1 rep" or "10 sets x 5 reps"
        // returns a tuple where the first item is the set count and the second is the rep count
        private static readonly Parser<Tuple<int, int>> setAndRepetitionCountParser =
            from _ in Parse.String("*")
            from setCount in Parse.Number.Select(int.Parse).Token(HorizontalWhitespaceParser.Parser)
            from __ in Parse.IgnoreCase("set").Then(x => Parse.IgnoreCase("s").Optional()).Token(HorizontalWhitespaceParser.Parser)
            from ___ in Parse.IgnoreCase("x").Token(HorizontalWhitespaceParser.Parser)
            from repetitionCount in Parse.Number.Select(int.Parse).Token(HorizontalWhitespaceParser.Parser)
            from ____ in Parse.IgnoreCase("rep").Then(x => Parse.IgnoreCase("s").Optional()).Token(HorizontalWhitespaceParser.Parser)
            from _____ in NewLineParser.Parser.Or(ParseExt.Default<NewLineType>().End())
            select Tuple.Create(setCount, repetitionCount);

        // parses an event matcher name. e.g. "before" or "after set"
        private static Parser<Unit> GetEventMatcherName(EventMatcherPreposition preposition, EventMatcherNoun? noun = null)
        {
            var parser = Parse.IgnoreCase(preposition.ToString()).ToUnit();

            if (noun.HasValue)
            {
                parser = parser
                    .Then(_ => HorizontalWhitespaceParser.Parser.AtLeastOnce())
                    .Then(_ => Parse.IgnoreCase(noun.ToString()))
                    .Then(__ => Parse.IgnoreCase("s").Optional())
                    .ToUnit();
            }

            return parser;
        }

        // parses a typed event matcher. e.g. "before set: ..." or "after rep: ..."
        // returns a matcher with action, where all specified actions are enclosed in a single sequence action
        private static Parser<MatcherWithAction> GetTypedEventMatcherWithActionParser<T>(Parser<Unit> nameParser, IContainerService containerService)
            where T : IEvent
            => 
                from _ in Parse.String("*")
                from __ in HorizontalWhitespaceParser.Parser.AtLeastOnce()
                from ___ in nameParser
                from ____ in Parse.Char(':').Token(HorizontalWhitespaceParser.Parser)
                from _____ in NewLineParser.Parser
                from actions in ActionListParser.GetParser(1, containerService)
                let action = new SequenceAction(actions)
                select new MatcherWithAction(new TypedEventMatcher<T>(), action);

        // parses a typed event matcher. e.g. "before set 3: ..." or "after reps first+1..last: ..."
        // returns a matcher with action, where all specified actions are enclosed in a single sequence action
        private static Parser<MatcherWithAction> GetNumberedEventMatcherWithActionParser<T>(
                Parser<Unit> nameParser,
                IContainerService containerService,
                Func<ExecutionContext, int> getActual,
                Func<ExecutionContext, int> getFirst,
                Func<ExecutionContext, int> getLast)
            where T : NumberedEvent
            => 
                from _ in Parse.String("*")
                from __ in HorizontalWhitespaceParser.Parser.AtLeastOnce()
                from ___ in nameParser
                from ____ in HorizontalWhitespaceParser.Parser.AtLeastOnce()
                from numericalConstraint in NumericalConstraintParser.GetParser(getActual, getFirst, getLast)
                from _____ in Parse.String(":").Token(HorizontalWhitespaceParser.Parser)
                from ______ in NewLineParser.Parser
                from actions in ActionListParser.GetParser(1, containerService)
                let action = new SequenceAction(actions)
                select new MatcherWithAction(new NumberedEventMatcher<T>(e => numericalConstraint(e.ExecutionContext)), action);

        // parses any matcher with an action. e.g. "before: ..." or "after sets 3..2..8: ..."
        // returns a matcher with action, where all specified actions are enclosed in a single sequence action
        private static Parser<MatcherWithAction> GetMatcherWithActionParser(IContainerService containerService)
        {
            var beforeSetNameParser = GetEventMatcherName(EventMatcherPreposition.Before, EventMatcherNoun.Set);
            var afterSetNameParser = GetEventMatcherName(EventMatcherPreposition.After, EventMatcherNoun.Set);
            var beforeRepNameParser =  GetEventMatcherName(EventMatcherPreposition.Before, EventMatcherNoun.Rep);
            var duringRepNameParser =  GetEventMatcherName(EventMatcherPreposition.During, EventMatcherNoun.Rep);
            var afterRepNameParser =  GetEventMatcherName(EventMatcherPreposition.After, EventMatcherNoun.Rep);

            return GetTypedEventMatcherWithActionParser<BeforeExerciseEvent>(GetEventMatcherName(EventMatcherPreposition.Before), containerService)
                .Or(GetTypedEventMatcherWithActionParser<AfterExerciseEvent>(GetEventMatcherName(EventMatcherPreposition.After), containerService))
                .Or(GetTypedEventMatcherWithActionParser<BeforeSetEvent>(beforeSetNameParser, containerService))
                .Or(GetTypedEventMatcherWithActionParser<AfterSetEvent>(afterSetNameParser, containerService))
                .Or(GetTypedEventMatcherWithActionParser<BeforeRepetitionEvent>(beforeRepNameParser, containerService))
                .Or(GetTypedEventMatcherWithActionParser<DuringRepetitionEvent>(duringRepNameParser, containerService))
                .Or(GetTypedEventMatcherWithActionParser<AfterRepetitionEvent>(afterRepNameParser, containerService))
                .Or(GetNumberedEventMatcherWithActionParser<BeforeSetEvent>(beforeSetNameParser, containerService, ec => ec.CurrentSet, ec => 1, ec => ec.CurrentExercise.SetCount))
                .Or(GetNumberedEventMatcherWithActionParser<AfterSetEvent>(afterSetNameParser, containerService, ec => ec.CurrentSet, ec => 1, ec => ec.CurrentExercise.SetCount))
                .Or(GetNumberedEventMatcherWithActionParser<BeforeRepetitionEvent>(beforeRepNameParser, containerService, ec => ec.CurrentRepetition, ec => 1, ec => ec.CurrentExercise.RepetitionCount))
                .Or(GetNumberedEventMatcherWithActionParser<DuringRepetitionEvent>(duringRepNameParser, containerService, ec => ec.CurrentRepetition, ec => 1, ec => ec.CurrentExercise.RepetitionCount))
                .Or(GetNumberedEventMatcherWithActionParser<AfterRepetitionEvent>(afterRepNameParser, containerService, ec => ec.CurrentRepetition, ec => 1, ec => ec.CurrentExercise.RepetitionCount));
        }

        // parses any number of matchers with their associated action
        private static Parser<IEnumerable<MatcherWithAction>> GetMatchersWithActionsParser(IContainerService containerService)
            =>  GetMatcherWithActionParser(containerService).DelimitedBy(NewLineParser.Parser.Token(HorizontalWhitespaceParser.Parser).AtLeastOnce());

        public static Parser<Exercise> GetParser(IContainerService containerService)
        {
            containerService.AssertNotNull(nameof(containerService));

            return
                from name in HeadingParser.GetParser(2)
                from _ in VerticalSeparationParser.Parser
                from setAndRepetitionCount in setAndRepetitionCountParser
                from __ in VerticalSeparationParser.Parser
                from matchersWithActions in GetMatchersWithActionsParser(containerService).Optional()
                select new Exercise(
                    containerService.Resolve<ILoggerService>(),
                    containerService.Resolve<ISpeechService>(),
                    name,
                    setAndRepetitionCount.Item1,
                    setAndRepetitionCount.Item2,
                    matchersWithActions.GetOrElse(Enumerable.Empty<MatcherWithAction>()));
        }

        private enum EventMatcherPreposition
        {
            Before,
            During,
            After
        }

        private enum EventMatcherNoun
        {
            Set,
            Rep
        }
    }
}