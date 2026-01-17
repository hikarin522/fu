using System.Collections.Immutable;

namespace Fu.Core.Models;

/// <summary>
/// 棋譜のノード（1手を表す）
/// </summary>
public sealed class MoveNode
{
    public Move Move { get; }
    public MoveNode? Parent { get; }
    public ImmutableList<MoveNode> Children { get; private set; } = [];

    /// <summary>このノードの深さ（手数、ルートは0）</summary>
    public int Depth { get; }

    /// <summary>このノードが属する分岐のインデックス（親から見た位置）</summary>
    public int BranchIndex { get; }

    public MoveNode(Move move, MoveNode? parent, int branchIndex = 0)
    {
        this.Move = move;
        this.Parent = parent;
        this.Depth = parent is null ? 1 : parent.Depth + 1;
        this.BranchIndex = branchIndex;
    }

    /// <summary>子ノードを追加</summary>
    public MoveNode AddChild(Move move)
    {
        // 同じ手が既にあればそれを返す（PlayerとCapturedPieceは比較から除外）
        var existing = this.Children.FirstOrDefault(c => IsSameMove(c.Move, move));
        if (existing is not null) {
            return existing;
        }

        var child = new MoveNode(move, this, this.Children.Count);
        this.Children = this.Children.Add(child);
        return child;
    }

    /// <summary>棋譜ツリー上で同じ手とみなすか（Player,CapturedPieceは無視）</summary>
    public static bool IsSameMove(Move a, Move b) =>
        a.To == b.To &&
        a.PieceType == b.PieceType &&
        a.IsPromotion == b.IsPromotion &&
        a.IsDrop == b.IsDrop &&
        a.From == b.From;

    /// <summary>ルートからこのノードまでのパスを取得</summary>
    public ImmutableList<MoveNode> GetPath()
    {
        var path = ImmutableList.CreateBuilder<MoveNode>();
        var current = this;
        while (current is not null) {
            path.Insert(0, current);
            current = current.Parent;
        }
        return path.ToImmutable();
    }

    /// <summary>ルートからこのノードまでの手を取得</summary>
    public ImmutableList<Move> GetMoves()
    {
        return [.. this.GetPath().Select(n => n.Move)];
    }

    /// <summary>分岐があるかどうか</summary>
    public bool HasBranches => this.Children.Count > 1;
}

/// <summary>
/// 棋譜ツリー全体を管理
/// </summary>
public sealed class MoveTree
{
    /// <summary>ルートノード（仮想的な開始点、手は持たない）</summary>
    private readonly List<MoveNode> _rootChildren = [];

    /// <summary>現在位置のノード（nullは初期局面）</summary>
    public MoveNode? CurrentNode { get; private set; }

    /// <summary>現在の手数</summary>
    public int CurrentDepth => this.CurrentNode?.Depth ?? 0;

    /// <summary>ルートの子ノード（1手目の選択肢）</summary>
    public IReadOnlyList<MoveNode> RootChildren => this._rootChildren;

    /// <summary>現在位置から見た次の手の選択肢</summary>
    public IReadOnlyList<MoveNode> NextMoves =>
        this.CurrentNode?.Children ?? (IReadOnlyList<MoveNode>)this._rootChildren;

    /// <summary>現在のメインラインの手順</summary>
    public ImmutableList<Move> CurrentLine =>
        this.CurrentNode?.GetMoves() ?? [];

    /// <summary>手を追加して進む</summary>
    public MoveNode AddMove(Move move)
    {
        if (this.CurrentNode is null) {
            // ルートに追加（PlayerとCapturedPieceは比較から除外）
            var existing = this._rootChildren.FirstOrDefault(c => MoveNode.IsSameMove(c.Move, move));
            if (existing is not null) {
                this.CurrentNode = existing;
                return existing;
            }

            var newNode = new MoveNode(move, null, this._rootChildren.Count);
            this._rootChildren.Add(newNode);
            this.CurrentNode = newNode;
            return newNode;
        }
        else {
            var child = this.CurrentNode.AddChild(move);
            this.CurrentNode = child;
            return child;
        }
    }

    /// <summary>手を追加するが現在位置は変更しない（ブランチ追加用）</summary>
    public MoveNode AddMoveWithoutAdvance(Move move)
    {
        if (this.CurrentNode is null) {
            // ルートに追加（PlayerとCapturedPieceは比較から除外）
            var existing = this._rootChildren.FirstOrDefault(c => MoveNode.IsSameMove(c.Move, move));
            if (existing is not null) {
                return existing;
            }

            var newNode = new MoveNode(move, null, this._rootChildren.Count);
            this._rootChildren.Add(newNode);
            return newNode;
        }
        else {
            return this.CurrentNode.AddChild(move);
        }
    }

    /// <summary>1手戻る</summary>
    public bool GoBack()
    {
        if (this.CurrentNode is null) {
            return false;
        }
        this.CurrentNode = this.CurrentNode.Parent;
        return true;
    }

    /// <summary>1手進む（メインライン）</summary>
    public bool GoForward()
    {
        var next = this.NextMoves;
        if (next.Count == 0) {
            return false;
        }
        this.CurrentNode = next[0];
        return true;
    }

    /// <summary>指定した分岐に進む</summary>
    public bool GoForwardBranch(int branchIndex)
    {
        var next = this.NextMoves;
        if (branchIndex < 0 || branchIndex >= next.Count) {
            return false;
        }
        this.CurrentNode = next[branchIndex];
        return true;
    }

    /// <summary>指定したノードに移動</summary>
    public void GoTo(MoveNode? node)
    {
        this.CurrentNode = node;
    }

    /// <summary>初期局面に戻る</summary>
    public void GoToStart()
    {
        this.CurrentNode = null;
    }

    /// <summary>メインラインの最後まで進む</summary>
    public void GoToEnd()
    {
        while (this.GoForward()) { }
    }

    /// <summary>ツリーをクリア</summary>
    public void Clear()
    {
        this._rootChildren.Clear();
        this.CurrentNode = null;
    }

    /// <summary>現在位置に分岐があるか</summary>
    public bool HasBranchesAtCurrent => this.NextMoves.Count > 1;

    /// <summary>ツリー内の分岐点の総数（子が2つ以上あるノードの数）</summary>
    public int TotalBranchCount
    {
        get
        {
            var count = 0;
            // ルートに複数の子があれば分岐
            if (this._rootChildren.Count > 1) {
                count++;
            }
            // 全ノードを走査して分岐点をカウント
            count += CountBranchesRecursive(this._rootChildren);
            return count;
        }
    }

    private static int CountBranchesRecursive(IReadOnlyList<MoveNode> nodes)
    {
        var count = 0;
        foreach (var node in nodes) {
            if (node.Children.Count > 1) {
                count++;
            }
            count += CountBranchesRecursive(node.Children);
        }
        return count;
    }

    /// <summary>全ての手（現在のラインのフラット表示用）</summary>
    public ImmutableList<Move> GetFlatMoves()
    {
        var moves = ImmutableList.CreateBuilder<Move>();
        var node = this.CurrentNode;

        // 現在位置までの手を取得
        if (node is not null) {
            moves.AddRange(node.GetMoves());
        }

        // 現在位置から先のメインラインを追加
        var current = node;
        while (true) {
            var children = current?.Children ?? (IReadOnlyList<MoveNode>)this._rootChildren;
            if (children.Count == 0) {
                break;
            }

            if (current is null && moves.Count > 0) {
                break; // 既にルートから取得済み
            }

            var next = children[0];
            if (current is not null || moves.Count == 0) {
                moves.Add(next.Move);
            }
            current = next;
        }

        return moves.ToImmutable();
    }

    /// <summary>全てのブランチ（各ラインの終端ノード）を取得</summary>
    public ImmutableList<MoveNode> GetAllBranchEndNodes()
    {
        var endNodes = ImmutableList.CreateBuilder<MoveNode>();
        CollectEndNodes(this._rootChildren, endNodes);
        return endNodes.ToImmutable();
    }

    private static void CollectEndNodes(IReadOnlyList<MoveNode> nodes, ImmutableList<MoveNode>.Builder endNodes)
    {
        foreach (var node in nodes) {
            if (node.Children.Count == 0) {
                // 終端ノード
                endNodes.Add(node);
            } else {
                // 子ノードを再帰的に探索
                CollectEndNodes(node.Children, endNodes);
            }
        }
    }

    /// <summary>指定したノードがどのブランチインデックスに属するか取得</summary>
    public int GetBranchIndexForNode(MoveNode? node)
    {
        if (node is null) {
            return 0;
        }

        var allEndNodes = this.GetAllBranchEndNodes();
        // ノードのパスを取得
        var nodePath = node.GetPath();

        for (var i = 0; i < allEndNodes.Count; i++) {
            var endNode = allEndNodes[i];
            var endPath = endNode.GetPath();

            // 現在のノードがこのブランチのパス上にあるかチェック
            if (nodePath.Count <= endPath.Count) {
                var match = true;
                for (var j = 0; j < nodePath.Count; j++) {
                    if (!ReferenceEquals(nodePath[j], endPath[j])) {
                        match = false;
                        break;
                    }
                }
                if (match) {
                    return i;
                }
            }
        }
        return 0;
    }
}
