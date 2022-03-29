﻿using Fumbbl.Gamefinder.Model.Event;

namespace Fumbbl.Gamefinder.Model
{
    public class GamefinderModel
    {
        private readonly MatchGraph _matchGraph;
        private EventQueue _eventQueue;

        public GamefinderModel(MatchGraph matchGraph, ILogger<GamefinderModel> logger)
        {
            _eventQueue = new(logger);
            _matchGraph = matchGraph;
            _matchGraph.MatchLaunched += MatchLaunched;
            Start();
        }

        public void Start()
        {
            _eventQueue?.Start();
        }

        public void Stop()
        {
            _eventQueue?.Stop();
        }

        private void MatchLaunched(object? sender, EventArgs e)
        {
            // Call to FUMBBL API to start the game

            // Tell MatchGraph which FFB Game ID needs to be redirected to
        }

        public async void ActivateAsync(Coach activatingCoach, IEnumerable<Team> activatingTeams)
        {
            await _eventQueue.DispatchAsync(() =>
            {
                var coachExists = _matchGraph.Contains(activatingCoach);
                if (!coachExists)
                {
                    _matchGraph.Add(activatingCoach);
                }
                _matchGraph.Ping(activatingCoach);

                var graphTeams = (_matchGraph.GetTeams(activatingCoach)).ToHashSet();
                foreach (var team in activatingTeams)
                {
                    if (!graphTeams.Contains(team))
                    {
                        _matchGraph.Add(team);
                    }
                    else
                    {
                        var graphTeam = graphTeams.Where(t => t.Equals(team)).First();
                        graphTeam.Update(team);
                    }
                }
                foreach (var team in graphTeams)
                {
                    if (!activatingTeams.Contains(team))
                    {
                        _matchGraph.Remove(team);
                    }
                }
            });
        }

        public async Task<Dictionary<Coach, IEnumerable<Team>>> GetCoachesAndTeams()
        {
            return await _eventQueue.Serialized<Dictionary<Coach, IEnumerable<Team>>>((result) =>
            {
                var dict = new Dictionary<Coach, IEnumerable<Team>>();
                var coaches = _matchGraph.GetCoaches();
                foreach (var coach in coaches)
                {
                    dict.Add(coach, _matchGraph.GetTeams(coach));
                }
                result.SetResult(dict);
            });
        }

        public async Task<IEnumerable<Team>> GetActivatedTeamsAsync(Coach coach)
        {
            return await _eventQueue.Serialized<Coach, List<Team>>((coach, result) =>
            {
                if (coach != null)
                {
                    result.SetResult(new List<Team>(_matchGraph.GetTeams(coach)));
                }
                else
                {
                    result.SetResult(new List<Team>());
                }
            }
            , coach);
        }

        public async Task<Dictionary<BasicMatch, MatchInfo>> GetMatches(Coach coach)
        {
            return await _eventQueue.Serialized<Coach, Dictionary<BasicMatch, MatchInfo>>((coach, result) =>
            {
                _matchGraph.Ping(coach);
                if (coach != null)
                {
                    Dictionary<BasicMatch, MatchInfo> dict = new Dictionary<BasicMatch, MatchInfo>();
                    var dialogMatch = _matchGraph.DialogManager.GetActiveDialog(coach);
                    foreach (var match in _matchGraph.GetMatches(coach))
                    {
                        dict.Add(match, new MatchInfo()
                        {
                            ShowDialog = match.Equals(dialogMatch)
                        });
                    }
                    result.SetResult(dict);
                }
                else
                {
                    result.SetResult(new());
                }
            }
            , coach);
        }

        public async Task MakeOffer(Coach coach, int myTeamId, int opponentTeamId)
        {
            await Act(coach, myTeamId, opponentTeamId, TeamAction.Accept);
        }

        public async Task CancelOffer(Coach coach, int myTeamId, int opponentTeamId)
        {
            await Act(coach, myTeamId, opponentTeamId, TeamAction.Cancel);
        }

        public async Task StartGame(Coach coach, int myTeamId, int opponentTeamId)
        {
            await Act(coach, myTeamId, opponentTeamId, TeamAction.Start);
        }

        private async Task Act(Coach coach, int myTeamId, int opponentTeamId, TeamAction action)
        {
            await _eventQueue.DispatchAsync(() =>
            {
                var match = _matchGraph.GetMatches(coach).SingleOrDefault(m => m.IsBetween(myTeamId, opponentTeamId)) as Match;
                var ownTeam = match?.Team1.Id == myTeamId ? match?.Team1 : match?.Team2;

                if (match != null && ownTeam != null && ownTeam.Coach.Id == coach.Id)
                {
                    match.Act(action, ownTeam);
                }
            });
        }
    }
}
