using Xunit;

// **DB を共有する結合テストは並列に実行しない。**
// xUnit は既定でテストクラスごとに並列実行するため、同じテーブルを触るクラスが
// 互いの行を消し合う。実機に当てるテストは順に流す。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
