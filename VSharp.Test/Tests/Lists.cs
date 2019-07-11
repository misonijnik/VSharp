using System;
using System.Collections.Generic;

namespace VSharp.Test.Tests
{
//    public class ListNode
//    {
//        public int Key;
//        public ListNode Next;
//    }

    [TestSvmFixture]
    public class Lists
    {
//        public void IncN(ListNode l, int n)
//        {
//            if (l == null || n == 0)
//                return;
//            l.Key += 1;
//            IncN(l.Next, n - 1);
//        }
//
//        public int DerefIncN(ListNode l, ListNode p)
//        {
//            if (l == null || p == null)
//            {
//                return 100500;
//            }
//            IncN(l, 10);
//            return p.Key;
//        }
//
//        public ListNode IncConcreteList(int n)
//        {
//            var l3 = new ListNode { Key = 30, Next = null };
//            var l2 = new ListNode { Key = 20, Next = l3 };
//            var l1 = new ListNode { Key = 10, Next = l2 };
//            IncN(l1, n);
//            return l1;
//        }
//
//        public ListNode IncSymbolicList(ListNode l, int n)
//        {
//            l.Next.Next.Next.Key += 1;
//            IncN(l, n);
//            return l;
//        }
//
//        private int a = 0;
//
//        private bool DoSmth()
//        {
//            a += 1;
//            return a > 3;
//        }

        [TestSvm]
        public bool Construct()
        {
            var a = new List<int>(4) { 1, 2, 3, 4 };
            var b = new int[4, 1];
            var c = new int[4] { 5, 6, 7, 8 };
            return a.Count == b.Length && b.Length == c.Length && c.Length == c[3] - 4;
        }

        [TestSvm]
        public int[] Mutate(int i)
        {
            var a = new int[] {1, 2, 3, 4, 5};
            a[i] = 10;
            return a;
        }

        [TestSvm]
        public int LowerBoundTest()
        {
            var c = new int[4, 2] { { 1, 1 }, { 2, 2 }, { 3, 3 }, { 4, 4 } };
            return c.GetLowerBound(1);
        }

        [TestSvm]
        public int LowerBoundExceptionTest(int[,] array)
        {
            return array.GetLowerBound(2);
        }

        [TestSvm]
        public int LowerBoundSymbolicTest(int[,] array, int dimension)
        {
            return array.GetLowerBound(dimension);
        }

        [TestSvm]
        public int UpperBoundTest()
        {
            var c = new int[4, 2] { { 1, 1 }, { 2, 2 }, { 3, 3 }, { 4, 4 } };
            return c.GetUpperBound(0);
        }

//        public void ClearTest()
//        {
//            var a = new int[4] { 5, 6, 7, 8 };
//            SystemArray.Clear(a, 1, 2);
//        }
//
//        public void Copy()
//        {
//            var a = new int[4] { 5, 6, 7, 8 };
//            var b = new int[3];
//            a.CopyTo(b, 1);
//        }

        [TestSvm]
        public int RankTest()
        {
            var c = new int[4, 2] { { 1, 1 }, { 2, 2 }, { 3, 3 }, { 4, 4 } };
            return c.Rank;
        }

        [TestSvm]
        public static int[] RetOneDArray1(bool flag1, bool flag2)
        {
            int[] arr = new int[5];
            if (flag1)
            {
                arr[1] = 42;
            }
            else if (flag2)
            {
                arr[1] = 89;
            }
            return arr;
        }

        [TestSvm]
        public static int[] RetOneDArray2(int n)
        {
            int[] arr = new int[n];
            if (n == 5)
            {
                arr[4] = 99;
                arr[1] = 42;
            }
            if (n == 8)
            {
                arr[1] = 89;
                arr[7] = 66;
            }
            return arr;
        }

        [TestSvm]
        public static Array RetSystemArray1(Array arr)
        {
            if (arr is int[])
            {
                var arrOne = arr as int[];
                arrOne[1] = 5;
            }
            else if (arr is int[,])
            {
                var arrOne = arr as int[,];
                arrOne[1,1] = 7;
            }
            return arr;
        }

        [TestSvm]
        public static Array RetSystemArray2(Array arr)
        {
            if (arr is int[])
            {
                var arrOne = arr as int[];
                arrOne[1] = 5;
            }
            if (arr is int[,])
            {
                var arrOne = arr as int[,];
                arrOne[1,1] = 7;
            }
            if (arr is int[,,])
            {
                var arrOne = arr as int[,,];
                arrOne[1,1,1] = 42;
            }
            return arr;
        }
    }

    public sealed class ListNode
    {
        public int Key;
        public ListNode Next;
    }

    public sealed class A
    {
        public int Field;
        public int OtherField;
    }
    [TestSvmFixture]
    public sealed class BinTreeNode
    {
        public int Key;
        public BinTreeNode Left = null;
        public BinTreeNode Right = null;

        public BinTreeNode(int x)
        {
            Key = x;
        }

        [TestSvm]
        public void Add(int x)
        {
            if (Key == x)
                return;
            if (x < Key)
            {
                if (Left == null)
                    Left = new BinTreeNode(x);
                else
                    Left.Add(x);
            }
            else
            {
                if (Right == null)
                    Right = new BinTreeNode(x);
                else
                    Right.Add(x);
            }
        }
        public void Add2(int x)
        {
//            if (Key == x)
//                return;
            if (x < Key)
            {
                if (Left == null)
                    Left = new BinTreeNode(x);
                else
                    Left.Add2(x);
            }
        }

        public bool Contains(int x)
        {
            if (Key == x)
                return true;
            if (x < Key)
            {
                if (Left == null)
                    return false;
                else
                    return Left.Contains(x);
            }
            else
            {
                if (Right == null)
                    return false;
                else
                    return Right.Contains(x);
            }
        }
    }

    [TestSvmFixture]
    public sealed class BinTree
    {
        private BinTreeNode _root = null;
        public int Key => _root.Key;

        [TestSvm]
        public void Add(int x)
        {
            if (_root == null)
                _root = new BinTreeNode(x);
            else
                _root.Add(x);
        }
        
        public void Add2(int x)
        {
            if (_root != null)
                _root.Add2(x);
        }

        public bool Contains(int x)
        {
            if (_root == null)
                return false;
            else
                return _root.Contains(x);
        }
    }

    internal static class SharedA
    {
        public static int Positivise(A a)
        {
            if (a.Field >= a.OtherField)
                return a.Field;
            a.Field++;
            return Positivise(a);
        }

        public static void IncField(A a, int n)
        {
            if (a == null || n <= 0)
                return;
            a.Field++;
            IncField(a, n - 1);
        }

        public static void AtLeastHundred(A a)
        {
            if (a == null)
                return;
            if (a.Field >= 100)
                return;
            a.Field++;
            AtLeastHundred(a);
        }

        public static void Fact(A a)
        {
            if (a == null)
                return;
            if (a.Field < 2)
            {
                a.OtherField = 1;
                return;
            }

            var f = a.Field;
            a.Field--;
            Fact(a);
            a.OtherField *= f;
        }

        public static void JustSetField(A a)
        {
            if (a == null || a.Field == a.OtherField)
                return;
            a.Field = a.OtherField;
            JustSetField(a);
        }

        public static void StrangeSum(A a)
        {
            if (a == null)
                return;
            if (a.OtherField <= 0)
                return;
            a.Field += a.OtherField;
            a.OtherField--;
            StrangeSum(a);
        }

        public static void AddOther(A a, int n)
        {
            if (a == null)
                return;
            if (n <= 0)
                return;
            a.Field += a.OtherField;
            AddOther(a, n - 1);
        }

        public static void MoveOtherToField(A a)
        {
            if (a == null)
                return;
            if (a.OtherField <= 0)
                return;
            a.Field++;
            a.OtherField--;
            MoveOtherToField(a);
        }

        public static bool IsFieldGreater(A a)
        {
            if (a == null || a.OtherField < 0)
                return false;
            if (a.OtherField == 0)
                return a.Field > 0;
            a.Field--;
            a.OtherField--;
            return IsFieldGreater(a);
        }

        public static void FibIter(A a, int n)
        {
            if (n <= 0)
                return;
            var tmp = a.Field;
            a.Field += a.OtherField;
            a.OtherField = tmp;
            FibIter(a, n-1);
        }

        public static int AddFields(A a)
        {
            if (a == null || a.Field < 0 || a.OtherField < 0)
                return 0;
            if (a.Field == 0)
                return a.OtherField;
            a.Field--;
            return 1 + AddFields(a);
        }

        public static bool FieldsAreEqual(A a)
        {
            if (a == null || a.Field < 0 || a.OtherField < 0)
                return false;
            if (a.Field == 0)
                return a.OtherField == 0;
            if (a.OtherField == 0)
                return false;
            a.Field--;
            a.OtherField--;
            return FieldsAreEqual(a);
        }
    }
    internal static class SharedTree
    {
        public static BinTreeNode Add(BinTreeNode tree, int x)
        {
            if (tree == null)
                return new BinTreeNode(x);
            if (x < tree.Key)
                tree.Left = Add(tree.Left, x);
            else if (x > tree.Key)
                tree.Right = Add(tree.Right, x);

            return tree;
        }

        public static bool Contains(BinTreeNode tree, int x)
        {
            if (tree == null)
                return false;
            if (tree.Key == x)
                return true;
            if (x < tree.Key)
                return Contains(tree.Left, x);
            return Contains(tree.Right, x);
        }

        public static BinTreeNode FromList(ListNode list)
        {
            if (list == null)
                return null;
            var tree = FromList(list.Next);
            if (tree == null)
                return new BinTreeNode(list.Key);
            Add(tree, list.Key);
            return tree;
        }

        public static int Max(BinTreeNode tree)
        {
            if (tree == null)
                return -1;
            if (tree.Right == null)
                return tree.Key;
            return Max(tree.Right);
        }
    }
    
     internal static class SharedList
    {
        public static ListNode RemoveOne(ListNode l, int x)
        {
            if (l == null)
                return null;
            if (l.Key == x)
                return l.Next;
            l.Next = RemoveOne(l.Next, x);
            return l;
        }

        public static ListNode RemoveAll(ListNode l, int x)
        {
            if (l == null)
                return null;
            var tail = RemoveAll(l.Next, x);
            if (l.Key == x)
                return tail;
            l.Next = tail;
            return l;
        }

        public static ListNode CreateList(int n)
        {
            if (n <= 0)
                return null;
            ListNode tail = CreateList(n - 1);
            ListNode head = new ListNode {Key = 0, Next = tail};
            return head;
        }

        public static ListNode CreateDecreasingList(int n)
        {
            if (n <= 0)
                return null;
            ListNode tail = CreateDecreasingList(n - 1);
            ListNode head = new ListNode {Key = n, Next = tail};
            return head;
        }

        public static int Length(ListNode l)
        {
            if (l == null)
                return 0;
            return 1 + Length(l.Next);
        }

        public static int Last(ListNode l)
        {
            if (l == null)
                return -1;
            if (l.Next == null)
                return l.Key;
            return Last(l.Next);
        }

        public static int Sum(ListNode l)
        {
            if (l == null)
                return 0;
            return l.Key + Sum(l.Next);
        }

        public static ListNode Reverse(ListNode l)
        {
            if (l == null || l.Next == null)
                return l;
            var h = Reverse(l.Next);
            l.Next.Next = l; // l.Next is now the last element
            l.Next = null;
            return h;
        }

        public static void Crop(ListNode l, int n)
        {
            if (n <= 0 || l == null)
                return;
            if (n == 1)
            {
                l.Next = null;
                return;
            }

            Crop(l.Next, n - 1);
        }

        public static ListNode LastNode(ListNode l)
        {
            if (l == null)
                return null;
            if (l.Next == null)
                return l;
            return LastNode(l.Next);
        }

        public static void Append(ListNode l1, ListNode l2)
        {
            if (l1 == null)
                throw new ArgumentException();
            var l1Last = LastNode(l1);
            l1Last.Next = l2;
        }

        public static bool Contains(ListNode l, int k)
        {
            if (l == null)
                return false;
            if (l.Key == k)
                return true;
            return Contains(l.Next, k);
        }

        public static void IncN(ListNode l)
        {
            if (l == null)
                return;
            l.Key += 1;
            IncN(l.Next);
        }

        public static void IncNwithN(ListNode l, int n)
        {
            if (l == null || n == 0)
                return;
            l.Key += 1;
            IncNwithN(l.Next, n - 1);
        }

        public static int Mult(int x, int y)
        {
            if (x <= 0)
                return 0;
            return y + Mult(x - 1, y);
        }

        public static ListNode CreateOnes(int n)
        {
            if (n <= 0)
                return null;
            ListNode tail = CreateOnes(n - 1);
            return new ListNode {Key = 1, Next = tail};
        }

        public static bool IsDecreasingFrom(ListNode l, int n)
        {
            if (l == null)
                return true;
            if (l.Key > n)
                return false;
            return IsDecreasingFrom(l.Next, l.Key);
        }

        public static int MaxThan(ListNode l, int max)
        {
            if (l == null)
                return max;
            if (l.Key > max)
                return MaxThan(l.Next, l.Key);
            return MaxThan(l.Next, max);
        }

        public static int Item(ListNode l, int i)
        {
            if (l == null)
                return -1;
            if (i == 0)
                return l.Key;
            return Item(l.Next, i - 1);
        }
    }

    public static class Container
    {
        public static int X = 0;
    }

    public class Bag
    {
        public int X;

        public Bag(int x)
        {
            X = x;
        }
    }

    public class First
    {
        public Second A = null;
        public int B;

        public int Get()
        {
            return B;
        }

        public void Inc()
        {
            B++;
        }
    }

    public class Second : First
    {
        private First b;

        public int Get()
        {
            if (b != null)
                return b.Get();
            return 0;
        }

        public void Inc()
        {
            b?.Inc();
        }
    }

    [TestSvmFixture]
    public static class RecursiveAccess
    {
        public static First G(First f)
        {
            if (f != null && f.A != null)
            {
                f.B++;
            }
            return f;
        }

        [TestSvm]
        public static int F(int x)
        {
            if (x > 10)
            {
                Container.X = x;
                return x;
            }
            var tmp = new Bag(Container.X);
            Container.X++;
            Container.X = F(Container.X);
            return Container.X + tmp.X;
        }

        [TestSvm]
        public static int G(int x)
        {
            return F(5) + 10;
        }

        [TestSvm]
        public static int NonEmptyPath(First f)
        {
            int res = 0;
            if (f != null && f.A != null)
            {
                f.A.B = 7;
                var p = G(f.A);
                if (p != null)
                {
                    res = p.B;
                }
            }
            return res;
        }

        [TestSvm]
        public static int TestStack(Second b)
        {
            if (b != null)
            {
                b.Inc();
                return b.Get();
            }
            return 0;
        }

//        [Ignore("Internal error: stack does not contain key (this, 600012F)!")]
        [TestSvm]
        public static void TestBinTree(BinTree tree, int x)
        {
            if (tree == null)
                return;
            tree.Add(x);
            if (!tree.Contains(x))
                throw new Exception();
        }

//        [Ignore("Internal error: stack does not contain key (this, 6000130)!")]
        [TestSvm]
        public static void TestBinTree2(BinTree tree, int x)
        {
            if (tree == null)
                return;
            tree.Add2(x);
        }
    }
}
