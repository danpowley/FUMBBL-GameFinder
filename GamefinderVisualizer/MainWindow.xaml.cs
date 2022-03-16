﻿using Fumbbl.Gamefinder.Model;
using Fumbbl.Gamefinder.Model.Event;
using GamefinderVisualizer.Models;
using GraphX.Common.Enums;
using GraphX.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace GamefinderVisualizer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MatchGraph _graph;
        private GamefinderGraph _renderedGraph = new();
        private readonly Dictionary<Team, DataVertex> tLookup = new();
        private readonly Dictionary<Coach, DataVertex> cLookup = new();
        private readonly Dictionary<Match, DataEdge> mLookup = new();
        private readonly Rect viewportRect = new(-400, -400, 800, 800);

        public MainWindow()
        {
            GamefinderModel gameFinder = new();
            _graph = new(gameFinder);

            InitializeComponent();

            ZoomControl.SetViewFinderVisibility(zoomctrl, Visibility.Hidden);

            InitializeGamefinder();
            InitializeGraph();

            Loaded += Window_Loaded;
        }

        private readonly List<Coach> coaches = new();
        private readonly Random r = new();
        private int cNum = 0, tNum = 0;

        private async Task Simulate()
        {
            var maxCoaches = 5;
            var addCoach = coaches.Count < maxCoaches || r.Next(0, 20) == 0;
            if (addCoach)
            {
                cNum++;
                Coach c = new() { Id = cNum, Name = $"Coach {cNum}" };
                await _graph.AddAsync(c);

                var numTeams = r.Next(3, 6) / 2;
                for (var i = 0; i < numTeams; i++)
                {
                    tNum++;
                    Team t = new(c) { Id = tNum, Name = $"Team {tNum}" };
                    await _graph.AddTeamToCoachAsync(t, c);
                    await _graph.AddAsync(t);
                }
            }

            if (coaches.Count > maxCoaches)
            {
                await _graph.RemoveAsync(coaches.First());
            }

            foreach (var c in coaches.ToArray())
            {
                var matches = await _graph.GetMatchesAsync(c);
                if (matches.Count > 0)
                {
                    var launchedMatch = matches.FirstOrDefault(m => m?.MatchState?.TriggerLaunchGame ?? false);

                    if (launchedMatch != null)
                    {
                        continue;
                    }

                    var match = matches[r.Next(matches.Count)];
                    if (!match.MatchState.IsHidden)
                    {
                        var ownTeam = match.Team1.Coach.Equals(c) ? match.Team1 : match.Team2;

                        if (!match.MatchState.IsDefault && r.Next(20) == 0)
                        {
                            await match.ActAsync(TeamAction.Cancel, ownTeam);
                        }
                        else
                        {
                            if (match.MatchState.TriggerStartDialog)
                            {
                                await match.ActAsync(TeamAction.Start, ownTeam);
                            }
                            else
                            {
                                await match.ActAsync(TeamAction.Accept, ownTeam);
                            }
                        }
                    }
                }
                c.Ping();
            }
        }

        private void InitializeGamefinder()
        {
            _graph.CoachAdded += Graph_CoachAdded;
            _graph.CoachRemoved += Graph_CoachRemoved;
            _graph.TeamAdded += Graph_TeamAdded;
            _graph.TeamRemoved += Graph_TeamRemoved;
            _graph.MatchAdded += Graph_MatchAdded;
            _graph.MatchRemoved += Graph_MatchRemoved;
            _graph.GraphUpdated += async (s, e) => await Graph_UpdatedAsync(s, e);

            _graph.Start();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Area.GenerateGraph(true, true);

            Task.Run(async () =>
            {
                while (true)
                {
                    Thread.Sleep(1000);
                    await Simulate();
                }
            });
        }

        private void InitializeGraph()
        {
            var logicCore = new GamefinderGraphLogicCore() { Graph = _renderedGraph };

            logicCore.DefaultLayoutAlgorithm = LayoutAlgorithmTypeEnum.Circular;
            logicCore.DefaultLayoutAlgorithmParams = logicCore.AlgorithmFactory.CreateLayoutParameters(LayoutAlgorithmTypeEnum.Circular);

            logicCore.DefaultOverlapRemovalAlgorithm = OverlapRemovalAlgorithmTypeEnum.FSA;
            logicCore.DefaultOverlapRemovalAlgorithmParams.HorizontalGap = 300;
            logicCore.DefaultOverlapRemovalAlgorithmParams.VerticalGap = 300;
            logicCore.DefaultEdgeRoutingAlgorithm = EdgeRoutingAlgorithmTypeEnum.None;
            logicCore.AsyncAlgorithmCompute = false;

            var vertexPositions = new Dictionary<DataVertex, GraphX.Measure.Point>();
            var parameters = new GamefinderLayoutParameters();
            logicCore.ExternalLayoutAlgorithm = new GamefinderLayout(_renderedGraph, vertexPositions, parameters);

            Area.LogicCore = logicCore;
        }

        #region MatchGraph Event Handlers
        private async Task Graph_UpdatedAsync(object? sender, EventArgs e)
        {
            await this.Dispatcher.InvokeAsync(() =>
            {
                _renderedGraph.Generate(Area);
                Area.ShowAllEdgesLabels(false);
                zoomctrl.ZoomToContent(viewportRect);
            });
        }

        private void Graph_MatchRemoved(object? sender, EventArgs e)
        {
            Match? match = ((MatchUpdatedArgs)e).Match as Match;

            if (match is not null && mLookup.ContainsKey(match))
            {
                _renderedGraph.RemoveEdge(mLookup[match]);
            }
        }

        private void Graph_MatchAdded(object? sender, EventArgs e)
        {
            Match? match = ((MatchUpdatedArgs)e).Match as Match;

            if (match is not null)
            {
                (var t1, var t2) = (match.Team1, match.Team2);
                var edge = new DataEdge(tLookup[t1], tLookup[t2]) { IsMatch = true, Match = match };
                _renderedGraph.AddEdge(edge);
            }
        }

        private void Graph_TeamRemoved(object? sender, EventArgs e)
        {
            Team? team = ((TeamUpdatedArgs)e).Team;

            if (team is not null)
            {
                _renderedGraph.RemoveVertex(tLookup[team]);
            }
        }

        private void Graph_TeamAdded(object? sender, EventArgs e)
        {
            Team? team = ((TeamUpdatedArgs)e).Team;

            if (team is not null)
            {
                var vertex = new DataVertex() { SortId = team.Id, VType = DataVertex.VertexType.Team, Label = team.Name };
                vertex.GroupId = team.Coach.Id;
                tLookup.Add(team, vertex);
                _renderedGraph.AddVertex(vertex);

                var cVertex = cLookup[team.Coach];
                cVertex.SortId = team.Id;
                var edge = new DataEdge(cVertex, vertex) { IsMatch = false, State = "", Match = null };
                _renderedGraph.AddEdge(edge);
            }

        }

        private void Graph_CoachRemoved(object? sender, EventArgs e)
        {
            Coach? coach = ((CoachUpdatedArgs)e).Coach;

            if (coach is not null && cLookup.ContainsKey(coach))
            {
                _renderedGraph.RemoveVertex(cLookup[coach]);
                coaches.Remove(coach);
                cLookup.Remove(coach);
            }
        }

        private void Graph_CoachAdded(object? sender, EventArgs e)
        {
            Coach? coach = ((CoachUpdatedArgs)e).Coach;

            if (coach is not null)
            {
                var vertex = new DataVertex() { SortId = 0, VType = DataVertex.VertexType.Coach, Coach = coach, Label = coach.Name };
                coaches.Add(coach);
                _renderedGraph.AddVertex(vertex);
                cLookup.Add(coach, vertex);
            }

        }
        #endregion


    }
}
