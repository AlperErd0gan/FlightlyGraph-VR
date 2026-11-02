using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Graph search over the loaded network, for GraphSearchView (shown step by step) and others:
/// breadth-first (layer by layer; with a depth limit, reachability within k stops) and
/// depth-first (one branch at a time, with backtracking).
/// Only routes the graph shows count (GraphLoader.PassesFilter: the timeline month, filters,
/// city groups). Neighbours are taken busiest route first, like ConnectionFinder.
/// </summary>
public static class GraphSearch
{
    /// <summary>One step of a search, in the order it happens.</summary>
    public struct Step
    {
        /// <summary>Airport reached (visit) or returned to (backtrack).</summary>
        public GraphNode node;
        /// <summary>Airport it was reached from (null for the start).</summary>
        public GraphNode parent;
        /// <summary>Route it was reached by (null for the start and for backtracks).</summary>
        public GraphEdge via;
        /// <summary>Stops from the start (BFS layer / DFS depth).</summary>
        public int depth;
        /// <summary>DFS: going back up to `node` after its branch is done.</summary>
        public bool backtrack;
    }

    /// <summary>Breadth-first from `start`: the start, then every airport one stop away, then two, ... (maxDepth stops at most).</summary>
    public static List<Step> Bfs(GraphLoader graph, GraphNode start, int maxDepth = int.MaxValue)
    {
        var steps = new List<Step> { new Step { node = start, depth = 0 } };
        var depth = new Dictionary<GraphNode, int> { [start] = 0 };
        var queue = new Queue<GraphNode>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            GraphNode current = queue.Dequeue();
            int d = depth[current];
            if (d >= maxDepth) continue;
            foreach (GraphEdge e in Neighbours(graph, current))
            {
                GraphNode next = Other(graph, e, current);
                if (next == null || depth.ContainsKey(next)) continue;
                depth[next] = d + 1;
                steps.Add(new Step { node = next, parent = current, via = e, depth = d + 1 });
                queue.Enqueue(next);
            }
        }
        return steps;
    }

    /// <summary>
    /// Depth-first from `start` (iterative, so deep branches cannot overflow the stack): each airport
    /// is visited once; when a branch has no unvisited neighbour left, a backtrack step goes back up.
    /// </summary>
    public static List<Step> Dfs(GraphLoader graph, GraphNode start)
    {
        var steps = new List<Step> { new Step { node = start, depth = 0 } };
        var visited = new HashSet<GraphNode> { start };
        var stack = new List<(GraphNode node, List<GraphEdge> edges, int next)> { (start, Neighbours(graph, start), 0) };
        while (stack.Count > 0)
        {
            int top = stack.Count - 1;
            (GraphNode node, List<GraphEdge> edges, int next) = stack[top];
            GraphNode child = null;
            GraphEdge via = null;
            while (next < edges.Count && child == null)
            {
                GraphNode candidate = Other(graph, edges[next], node);
                if (candidate != null && !visited.Contains(candidate))
                {
                    child = candidate;
                    via = edges[next];
                }
                next++;
            }
            stack[top] = (node, edges, next);
            if (child != null)
            {
                visited.Add(child);
                steps.Add(new Step { node = child, parent = node, via = via, depth = stack.Count });
                stack.Add((child, Neighbours(graph, child), 0));
            }
            else
            {
                stack.RemoveAt(top);
                if (stack.Count > 0) steps.Add(new Step { node = stack[stack.Count - 1].node, depth = stack.Count - 1, backtrack = true });
            }
        }
        return steps;
    }

    /// <summary>Shown routes of an airport, busiest first.</summary>
    private static List<GraphEdge> Neighbours(GraphLoader graph, GraphNode node)
    {
        var list = new List<GraphEdge>();
        foreach (GraphEdge e in graph.EdgesOf(node.id))
        {
            if (graph.PassesFilter(e)) list.Add(e);
        }
        list.Sort((x, y) => graph.FilteredWeight(y).CompareTo(graph.FilteredWeight(x)));
        return list;
    }

    private static GraphNode Other(GraphLoader graph, GraphEdge e, GraphNode node)
    {
        string id = e.sourceId == node.id ? e.targetId : e.sourceId;
        return graph.Nodes.TryGetValue(id, out GraphNode other) ? other : null;
    }
}
