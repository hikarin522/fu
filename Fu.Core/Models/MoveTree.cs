using System.Collections.Immutable;

using Fu.Core.Collections;

namespace Fu.Core.Models;

/// <summary>
/// 棋譜のノード（TreeNode&lt;Move&gt;のラッパー、評価キャッシュを追加）
/// </summary>
public sealed class MoveNode
{
    private readonly TreeNode<Move> _node;

    /// <summary>この手</summary>
    public Move Move => this._node.Value;

    /// <summary>親ノード</summary>
    public MoveNode? Parent { get; }

    /// <summary>子ノード</summary>
    public IReadOnlyList<MoveNode> Children => this._children;
    private readonly List<MoveNode> _children = [];

    /// <summary>このノードの深さ（手数、ルートは0）</summary>
    public int Depth => this._node.Depth;

    /// <summary>このノードが属する分岐のインデックス（親から見た位置）</summary>
    public int BranchIndex => this._node.BranchIndex;

    /// <summary>この局面の評価キャッシュ</summary>
    public CachedEvaluation? CachedEvaluation { get; set; }

    /// <summary>内部のTreeNodeへの参照</summary>
    internal TreeNode<Move> InnerNode => this._node;

    internal MoveNode(TreeNode<Move> node, MoveNode? parent)
    {
        this._node = node;
        this.Parent = parent;
    }

    /// <summary>子ノードを追加</summary>
    public MoveNode AddChild(Move move)
    {
        // 同じ手が既にあればそれを返す
        var existing = this._children.FirstOrDefault(c => IsSameMove(c.Move, move));
        if (existing is not null) {
            return existing;
        }

        var childTreeNode = this._node.AddChild(move);
        var child = new MoveNode(childTreeNode, this);
        this._children.Add(child);
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
    public IReadOnlyList<MoveNode> GetPath()
    {
        // Depth を使って配列サイズを事前確定（Insert(0,...) による O(n²) を回避）
        var path = new MoveNode[this.Depth];
        var current = this;
        for (var i = this.Depth - 1; i >= 0 && current is not null; i--) {
            path[i] = current;
            current = current.Parent;
        }
        return path;
    }

    /// <summary>ルートからこのノードまでの手を取得</summary>
    public IReadOnlyList<Move> GetMoves()
    {
        var moves = new Move[this.Depth];
        var current = this;
        for (var i = this.Depth - 1; i >= 0 && current is not null; i--) {
            moves[i] = current.Move;
            current = current.Parent;
        }
        return moves;
    }

    /// <summary>分岐があるかどうか</summary>
    public bool HasBranches => this._children.Count > 1;
}

/// <summary>
/// 棋譜ツリー全体を管理（Tree&lt;Move&gt;のラッパー）
/// </summary>
public sealed class MoveTree
{
    private readonly Tree<Move> _tree = new();
    private readonly Dictionary<TreeNode<Move>, MoveNode> _nodeMap = [];
    private readonly List<MoveNode> _rootChildren = [];

    /// <summary>現在位置のノード（nullは初期局面）</summary>
    public MoveNode? CurrentNode { get; private set; }

    /// <summary>開始局面（CurrentNode=null）の評価キャッシュ</summary>
    public CachedEvaluation? RootEvaluation { get; set; }

    /// <summary>現在の手数</summary>
    public int CurrentDepth => this._tree.CurrentDepth;

    /// <summary>ルートの子ノード（1手目の選択肢）</summary>
    public IReadOnlyList<MoveNode> RootChildren => this._rootChildren;

    /// <summary>現在位置から見た次の手の選択肢</summary>
    public IReadOnlyList<MoveNode> NextMoves =>
        this.CurrentNode?.Children ?? (IReadOnlyList<MoveNode>)this._rootChildren;

    /// <summary>現在のメインラインの手順</summary>
    public IReadOnlyList<Move> CurrentLine =>
        this.CurrentNode?.GetMoves() ?? [];

    /// <summary>手を追加して進む</summary>
    public MoveNode AddMove(Move move)
    {
        if (this.CurrentNode is null) {
            // ルートに追加
            var existing = this._rootChildren.FirstOrDefault(c => MoveNode.IsSameMove(c.Move, move));
            if (existing is not null) {
                this.CurrentNode = existing;
                this._tree.GoTo(existing.InnerNode);
                return existing;
            }

            var treeNode = this._tree.Add(move);
            var newNode = new MoveNode(treeNode, null);
            this._rootChildren.Add(newNode);
            this._nodeMap[treeNode] = newNode;
            this.CurrentNode = newNode;
            return newNode;
        }
        else {
            var child = this.CurrentNode.AddChild(move);
            this._tree.GoTo(child.InnerNode);
            this._nodeMap.TryAdd(child.InnerNode, child);
            this.CurrentNode = child;
            return child;
        }
    }

    /// <summary>手を追加するが現在位置は変更しない（ブランチ追加用）</summary>
    public MoveNode AddMoveWithoutAdvance(Move move)
    {
        if (this.CurrentNode is null) {
            var existing = this._rootChildren.FirstOrDefault(c => MoveNode.IsSameMove(c.Move, move));
            if (existing is not null) {
                return existing;
            }

            var treeNode = this._tree.AddWithoutAdvance(move);
            var newNode = new MoveNode(treeNode, null);
            this._rootChildren.Add(newNode);
            this._nodeMap[treeNode] = newNode;
            return newNode;
        }
        else {
            var child = this.CurrentNode.AddChild(move);
            this._nodeMap.TryAdd(child.InnerNode, child);
            return child;
        }
    }

    /// <summary>1手戻る</summary>
    public bool GoBack()
    {
        if (this.CurrentNode is null) {
            return false;
        }
        this.CurrentNode = this.CurrentNode.Parent;
        this._tree.GoBack();
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
        this._tree.GoForward();
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
        this._tree.GoForwardBranch(branchIndex);
        return true;
    }

    /// <summary>指定したノードに移動</summary>
    public void GoTo(MoveNode? node)
    {
        this.CurrentNode = node;
        this._tree.GoTo(node?.InnerNode);
    }

    /// <summary>初期局面に戻る</summary>
    public void GoToStart()
    {
        this.CurrentNode = null;
        this._tree.GoToStart();
    }

    /// <summary>メインラインの最後まで進む</summary>
    public void GoToEnd()
    {
        while (this.GoForward()) { }
    }

    /// <summary>ツリーをクリア</summary>
    public void Clear()
    {
        this._tree.Clear();
        this._rootChildren.Clear();
        this._nodeMap.Clear();
        this.CurrentNode = null;
        this.RootEvaluation = null;
    }

    /// <summary>現在位置に分岐があるか</summary>
    public bool HasBranchesAtCurrent => this.NextMoves.Count > 1;

    /// <summary>ツリー内の分岐点の総数（子が2つ以上あるノードの数）</summary>
    public int TotalBranchCount => this._tree.TotalBranchCount;

    /// <summary>全ての手（現在のラインのフラット表示用）</summary>
    public IReadOnlyList<Move> GetFlatMoves()
    {
        var moves = new List<Move>();
        var node = this.CurrentNode;

        if (node is not null) {
            moves.AddRange(node.GetMoves());
        }

        var current = node;
        while (true) {
            var children = current?.Children ?? (IReadOnlyList<MoveNode>)this._rootChildren;
            if (children.Count == 0) {
                break;
            }

            if (current is null && moves.Count > 0) {
                break;
            }

            var next = children[0];
            if (current is not null || moves.Count == 0) {
                moves.Add(next.Move);
            }
            current = next;
        }

        return moves;
    }

    /// <summary>全てのブランチ（各ラインの終端ノード）を取得</summary>
    public IReadOnlyList<MoveNode> GetAllBranchEndNodes()
    {
        var endNodes = new List<MoveNode>();
        CollectEndNodes(this._rootChildren, endNodes);
        return endNodes;
    }

    private static void CollectEndNodes(IReadOnlyList<MoveNode> nodes, List<MoveNode> endNodes)
    {
        foreach (var node in nodes) {
            if (node.Children.Count == 0) {
                endNodes.Add(node);
            } else {
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
        var nodePath = node.GetPath();

        for (var i = 0; i < allEndNodes.Count; i++) {
            var endNode = allEndNodes[i];
            var endPath = endNode.GetPath();

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
