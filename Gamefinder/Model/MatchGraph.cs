﻿
using ConcurrentCollections;
using Fumbbl.Gamefinder.Model.Event;
using Fumbbl.Gamefinder.Model.Store;
using System.Collections.Concurrent;

namespace Fumbbl.Gamefinder.Model
{
    public class MatchGraph
    {
        private EventQueue _eventQueue;
        private readonly TeamStore _teams;
        private readonly CoachStore _coaches;
        private readonly MatchStore _matches;
        private readonly DialogManager _dialogManager;
        public readonly ILogger<MatchGraph> Logger;

        public event EventHandler? CoachAdded;
        public event EventHandler? CoachRemoved;
        public event EventHandler? TeamAdded;
        public event EventHandler? TeamRemoved;
        public event EventHandler? MatchAdded;
        public event EventHandler? MatchRemoved;
        public event EventHandler? GraphUpdated;
        public event EventHandler? MatchLaunched;

        public DialogManager DialogManager => _dialogManager;

        public bool IsDialogActive(Match match) => _dialogManager.IsDialogActive(match);

        private readonly TimeSpan TickTimeout = TimeSpan.FromSeconds(1);
        private bool _started;

        public MatchGraph(ILogger<MatchGraph> logger)
        {
            Logger = logger;
            _teams = new(Logger);
            _coaches = new(Logger);
            _matches = new(Logger);
            _dialogManager = new(Logger);
            _eventQueue = new(Logger);
            _eventQueue.Tick += (sender, args) =>
            {
                Tick();
                GraphUpdated?.Invoke((object)this, EventArgs.Empty);
            };
        }


        private void Tick()
        {
            foreach (var match in _matches.GetMatches())
            {
                (match as Match)?.Tick();
            }

            foreach (var coach in _coaches.GetCoaches())
            {
                if (_coaches.IsTimedOut(coach))
                {
                    Logger.LogDebug($"Timed out {coach}");
                    Remove(coach);
                }
            }
        }

        public void Ping(Coach coach)
        {
            _coaches.Ping(coach);
        }
        /*
        public async Task<bool> Contains(Coach coach) => await Serialized<Coach, bool>(InternalContains, coach);
        public async Task RemoveAsync(BasicMatch match) => await DispatchAsync(() => InternalRemoveMatch(match));
        public async Task<List<BasicMatch>> GetMatchesAsync(Coach coach) => await Serialized<Coach, List<BasicMatch>>(InternalGetMatches, coach);
        public async Task<List<BasicMatch>> GetMatchesAsync() => await Serialized<List<BasicMatch>>(InternalGetMatches);
        public async Task<List<Coach>> GetCoachesAsync() => await Serialized<List<Coach>>(InternalGetCoaches);
        public async Task<List<Team>> GetTeamsAsync() => await Serialized<List<Team>>(InternalGetTeams);
        public async Task<List<Team>> GetTeamsAsync(Coach coach) => await Serialized<Coach, List<Team>>(InternalGetTeams, coach);
        public async Task<BasicMatch?> GetMatchAsync(Team team1, Team team2) => await Serialized<Team, Team, BasicMatch?>(InternalGetMatch, team1, team2);
        public async Task TriggerLaunchGameAsync(BasicMatch match) => await DispatchAsync(() => InternalTriggerLaunchGame(match));
        public async Task TriggerStartDialogAsync(Match match) => await DispatchAsync(() => InternalTriggerStartDialog(match));
        public async Task ClearDialogAsync(Match match) => await DispatchAsync(() => InternalClearDialog(match));
        public async Task<BasicMatch?> GetStartDialogMatch(Coach coach) => await Serialized<Coach, BasicMatch?>(InternalGetStartDialogMatch, coach);

        public async Task Reset() => await DispatchAsync(() => InternalReset());
        */

        public void Reset()
        {
            _dialogManager.Clear();
            _matches.Clear();
            _teams.Clear();
            _coaches.Clear();
        }

        private void GetStartDialogMatch(Coach coach, TaskCompletionSource<BasicMatch?> result)
        {
            result.SetResult(_dialogManager.GetActiveDialog(coach));
        }

        internal void ClearDialog(Match match)
        {
            Logger.LogDebug($"Clearing StartDialog for match {match}");
            _dialogManager.Remove(match);
        }

        public void TriggerStartDialog(Match match)
        {
            Logger.LogDebug($"Adding StartDialog for match {match}");
            _dialogManager.Add(match);
        }

        public void TriggerLaunchGame(BasicMatch match)
        {
            Logger.LogDebug($"Launching Match {match}");
            var coach1 = match.Team1.Coach;
            var coach2 = match.Team2.Coach;

            _dialogManager.Remove(match);
            _dialogManager.Remove(coach1);
            _dialogManager.Remove(coach2);

            coach1.Lock();
            coach2.Lock();

            foreach (var m in _teams.GetTeams(coach1).Concat(_teams.GetTeams(coach2)).SelectMany(t => _matches.GetMatches(t)))
            {
                if (!m.Equals(match) && m is Match m1)
                {
                    m1.Act(TeamAction.Cancel);
                }
            }
            MatchLaunched?.Invoke(this, new MatchUpdatedArgs(match));
        }

        public IEnumerable<Coach> GetCoaches()
            => new List<Coach>(_coaches.GetCoaches());

        public IEnumerable<Team> GetTeams()
            => new List<Team>(_teams.GetTeams());

        public IEnumerable<Team> GetTeams(Coach coach)
            => _teams.GetTeams(coach);

        public IEnumerable<BasicMatch> GetMatches()
            => new List<BasicMatch>(_matches.GetMatches());

        public IEnumerable<BasicMatch> GetMatches(Coach coach)
            => new List<BasicMatch>(_teams.GetTeams(coach).SelectMany(t => _matches.GetMatches(t)));

        public BasicMatch? GetMatch(Team team1, Team team2)
            => _matches.GetMatches(team1).Where(m => m.Includes(team2)).FirstOrDefault();

        public bool Contains(Coach coach)
            => _coaches.Contains(coach);


        public void Add(Team team)
        {
            Logger.LogDebug($"Adding team {team}");
            if (team is null || _teams.Contains(team))
            {
                return;
            }

            if (!_coaches.Contains(team.Coach))
            {
                _coaches.Add(team.Coach);
                CoachAdded?.Invoke(this, new CoachUpdatedArgs { Coach = team.Coach });
            }

            _teams.Add(team);
            TeamAdded?.Invoke(this, new TeamUpdatedArgs { Team = team });
            foreach (var opponent in _teams.GetTeams())
            {
                if (team is not null && team.IsOpponentAllowed(opponent) && !opponent.Coach.Locked)
                {
                    var match = new Match(this, opponent, team);
                    _matches.Add(match);
                    MatchAdded?.Invoke(this, new MatchUpdatedArgs(match));
                }
            }
        }

        public void Remove(Team team)
        {
            Logger.LogDebug($"Removing team {team}");
            if (team is null || !_teams.Contains(team))
            {
                return;
            }
            _dialogManager.Remove(team);

            foreach (var match in _matches.GetMatches(team))
            {
                var t = match.GetOpponent(team);
                if (t is not null)
                {
                    _matches.Remove(match);
                    if (match.MatchState.TriggerLaunchGame)
                    {
                        match.Team1.Coach.Unlock();
                        match.Team2.Coach.Unlock();
                    }

                    _dialogManager.Remove(match);
                    MatchRemoved?.Invoke(this, new MatchUpdatedArgs(match));

                }
            }
            _matches.Remove(team);
            _teams.Remove(team);
            TeamRemoved?.Invoke(this, new TeamUpdatedArgs { Team = team });
        }

        public void Remove(BasicMatch match)
        {
            Logger.LogDebug($"Removing Match {match}");
            if (match is null || !_matches.Contains(match))
            {
                return;
            }
            _dialogManager.Remove(match);

            _matches.Remove(match);
            if (match.MatchState.TriggerLaunchGame)
            {
                match.Team1.Coach.Unlock();
                match.Team2.Coach.Unlock();
            }

            MatchRemoved?.Invoke(this, new MatchUpdatedArgs(match));
        }

        public void Add(Coach coach)
        {
            Logger.LogDebug($"Adding coach {coach}");
            if (!_coaches.Contains(coach))
            {
                _coaches.Add(coach);
                CoachAdded?.Invoke(this, new CoachUpdatedArgs { Coach = coach });
            }
        }

        public void Remove(Coach coach)
        {
            Logger.LogDebug($"Removing coach {coach}");
            _dialogManager.Remove(coach);

            foreach (var team in _teams.GetTeams(coach))
            {
                Remove(team);
            }
            _coaches.Remove(coach);
            CoachRemoved?.Invoke(this, new CoachUpdatedArgs { Coach = coach });
        }
    }
}
