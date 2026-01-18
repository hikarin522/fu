namespace Fu.Core.Collections;

/// <summary>
/// 汎用的なN分木のノード
/// </summary>
/// <typeparam name="T">ノードが保持する値の型</typeparam>
public class TreeNode<T>
{
    private readonly List<TreeNode<T>> _children = [];

    /// <summary>このノードが保持する値</summary>
    public T Value { get; }

    /// <summary>親ノード（ルートの場合はnull）</summary>
    public TreeNode<T>? Parent { get; }

    /// <summary>子ノードのリスト</summary>
    public IReadOnlyList<TreeNode<T>> Children => this._children;

    /// <summary>このノードの深さ（ルートの子は1）</summary>
    public int Depth { get; }

    /// <summary>このノードが属する分岐のインデックス（親から見た位置）</summary>
    public int BranchIndex { get; }

    public TreeNode(T value, TreeNode<T>? parent, int branchIndex = 0)
    {
        this.Value = value;
        this.Parent = parent;
        this.Depth = parent is null ? 1 : parent.Depth + 1;
        this.BranchIndex = branchIndex;
    }

    /// <summary>子ノードを追加</summary>
    public TreeNode<T> AddChild(T value)
    {
        var child = new TreeNode<T>(value, this, this._children.Count);
        this._children.Add(child);
        return child;
    }

    /// <summary>既存の子を検索し、なければ追加</summary>
    public TreeNode<T> GetOrAddChild(T value, Func<T, T, bool> equals)
    {
        var existing = this.Children.FirstOrDefault(c => equals(c.Value, value));
        if (existing is not null) {
            return existing;
        }
        return this.AddChild(value);
    }

    /// <summary>ルートからこのノードまでのパスを取得</summary>
    public IReadOnlyList<TreeNode<T>> GetPath()
    {
        var path = new List<TreeNode<T>>();
        var current = this;
        while (current is not null) {
            path.Add(current);
            current = current.Parent;
        }
        path.Reverse();
        return path;
    }

    /// <summary>ルートからこのノードまでの値を取得</summary>
    public IReadOnlyList<T> GetValues() =>
        this.GetPath().Select(n => n.Value).ToList();

    /// <summary>分岐があるかどうか</summary>
    public bool HasBranches => this.Children.Count > 1;

    /// <summary>葉ノードかどうか</summary>
    public bool IsLeaf => this.Children.Count == 0;
}

/// <summary>
/// 汎用的なN分木（現在位置のカーソルを持つ）
/// </summary>
/// <typeparam name="T">ノードが保持する値の型</typeparam>
public class Tree<T>
{
    private readonly List<TreeNode<T>> _rootChildren = [];

    /// <summary>現在位置のノード（nullはルート位置）</summary>
    public TreeNode<T>? CurrentNode { get; private set; }

    /// <summary>現在の深さ</summary>
    public int CurrentDepth => this.CurrentNode?.Depth ?? 0;

    /// <summary>ルートの子ノード</summary>
    public IReadOnlyList<TreeNode<T>> RootChildren => this._rootChildren;

    /// <summary>現在位置から見た次のノードの選択肢</summary>
    public IReadOnlyList<TreeNode<T>> NextNodes =>
        this.CurrentNode?.Children ?? (IReadOnlyList<TreeNode<T>>)this._rootChildren;

    /// <summary>現在のラインの値</summary>
    public IReadOnlyList<T> CurrentLine =>
        this.CurrentNode?.GetValues() ?? [];

    /// <summary>値を追加して進む</summary>
    public TreeNode<T> Add(T value)
    {
        if (this.CurrentNode is null) {
            var newNode = new TreeNode<T>(value, null, this._rootChildren.Count);
            this._rootChildren.Add(newNode);
            this.CurrentNode = newNode;
            return newNode;
        }
        else {
            var child = this.CurrentNode.AddChild(value);
            this.CurrentNode = child;
            return child;
        }
    }

    /// <summary>既存ノードを検索し、あれば進む。なければ追加して進む</summary>
    public TreeNode<T> GetOrAdd(T value, Func<T, T, bool> equals)
    {
        if (this.CurrentNode is null) {
            var existing = this._rootChildren.FirstOrDefault(c => equals(c.Value, value));
            if (existing is not null) {
                this.CurrentNode = existing;
                return existing;
            }

            var newNode = new TreeNode<T>(value, null, this._rootChildren.Count);
            this._rootChildren.Add(newNode);
            this.CurrentNode = newNode;
            return newNode;
        }
        else {
            var child = this.CurrentNode.GetOrAddChild(value, equals);
            this.CurrentNode = child;
            return child;
        }
    }

    /// <summary>値を追加するが現在位置は変更しない</summary>
    public TreeNode<T> AddWithoutAdvance(T value)
    {
        if (this.CurrentNode is null) {
            var newNode = new TreeNode<T>(value, null, this._rootChildren.Count);
            this._rootChildren.Add(newNode);
            return newNode;
        }
        else {
            return this.CurrentNode.AddChild(value);
        }
    }

    /// <summary>既存ノードを検索し、あれば返す。なければ追加（位置は変更しない）</summary>
    public TreeNode<T> GetOrAddWithoutAdvance(T value, Func<T, T, bool> equals)
    {
        if (this.CurrentNode is null) {
            var existing = this._rootChildren.FirstOrDefault(c => equals(c.Value, value));
            if (existing is not null) {
                return existing;
            }

            var newNode = new TreeNode<T>(value, null, this._rootChildren.Count);
            this._rootChildren.Add(newNode);
            return newNode;
        }
        else {
            return this.CurrentNode.GetOrAddChild(value, equals);
        }
    }

    /// <summary>1つ戻る</summary>
    public bool GoBack()
    {
        if (this.CurrentNode is null) {
            return false;
        }
        this.CurrentNode = this.CurrentNode.Parent;
        return true;
    }

    /// <summary>1つ進む（メインライン）</summary>
    public bool GoForward()
    {
        var next = this.NextNodes;
        if (next.Count == 0) {
            return false;
        }
        this.CurrentNode = next[0];
        return true;
    }

    /// <summary>指定した分岐に進む</summary>
    public bool GoForwardBranch(int branchIndex)
    {
        var next = this.NextNodes;
        if (branchIndex < 0 || branchIndex >= next.Count) {
            return false;
        }
        this.CurrentNode = next[branchIndex];
        return true;
    }

    /// <summary>指定したノードに移動</summary>
    public void GoTo(TreeNode<T>? node) => this.CurrentNode = node;

    /// <summary>ルートに戻る</summary>
    public void GoToStart() => this.CurrentNode = null;

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
    public bool HasBranchesAtCurrent => this.NextNodes.Count > 1;

    /// <summary>ツリー内の分岐点の総数</summary>
    public int TotalBranchCount
    {
        get
        {
            var count = 0;
            if (this._rootChildren.Count > 1) {
                count++;
            }
            count += CountBranchesRecursive(this._rootChildren);
            return count;
        }
    }

    private static int CountBranchesRecursive(IReadOnlyList<TreeNode<T>> nodes)
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

    /// <summary>全てのブランチの終端ノードを取得</summary>
    public IReadOnlyList<TreeNode<T>> GetAllLeafNodes()
    {
        var leafNodes = new List<TreeNode<T>>();
        CollectLeafNodes(this._rootChildren, leafNodes);
        return leafNodes;
    }

    private static void CollectLeafNodes(IReadOnlyList<TreeNode<T>> nodes, List<TreeNode<T>> leafNodes)
    {
        foreach (var node in nodes) {
            if (node.Children.Count == 0) {
                leafNodes.Add(node);
            } else {
                CollectLeafNodes(node.Children, leafNodes);
            }
        }
    }

    /// <summary>指定したノードがどのブランチインデックスに属するか取得</summary>
    public int GetBranchIndexForNode(TreeNode<T>? node)
    {
        if (node is null) {
            return 0;
        }

        var allLeafNodes = this.GetAllLeafNodes();
        var nodePath = node.GetPath();

        for (var i = 0; i < allLeafNodes.Count; i++) {
            var leafNode = allLeafNodes[i];
            var leafPath = leafNode.GetPath();

            if (nodePath.Count <= leafPath.Count) {
                var match = true;
                for (var j = 0; j < nodePath.Count; j++) {
                    if (!ReferenceEquals(nodePath[j], leafPath[j])) {
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

    /// <summary>PreOrder（先行順）で全ノードを列挙</summary>
    public IEnumerable<TreeNode<T>> PreOrderTraversal()
    {
        foreach (var root in this._rootChildren) {
            foreach (var node in PreOrderTraversalNode(root)) {
                yield return node;
            }
        }
    }

    private static IEnumerable<TreeNode<T>> PreOrderTraversalNode(TreeNode<T> node)
    {
        yield return node;
        foreach (var child in node.Children) {
            foreach (var descendant in PreOrderTraversalNode(child)) {
                yield return descendant;
            }
        }
    }
}
