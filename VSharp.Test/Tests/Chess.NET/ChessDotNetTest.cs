using System;
using System.Collections.Generic;
using System.Reflection;
using ChessDotNet;
using ChessDotNet.Pieces;
using NUnit.Framework;
using VSharp.Interpreter.IL;

namespace VSharp.Test
{
    [TestFixture]
    public class ChessDotNetTest
    {
        [Test]
        public static void ExploreWholeChessDotNetAssembly()
        {
            var name = "ChessDotNet";
            var svm = new SVM(new ILInterpreter());
            //SVM.ConfigureSimplifier(new Z3Simplifier()); can be used to enable Z3-based simplification (not recommended)
            svm.ConfigureSolver(new SmtSolverWrapper<Microsoft.Z3.AST>(new Z3Solver()));
            var assembly = Assembly.Load(name);
            var ignoredLibs = new List<string>();
            svm.Run(assembly, ignoredLibs);
        }
    }

    [TestSvmFixture]
    public class CheckMate
    {
        [TestSvm]
        public static void CheckMateIsPossible(Move move)
        {
            if (move == null) return;
            GameCreationData data = new GameCreationData();
            Piece[][] board = new Piece[8][];
            board[0] = new Piece[8];
            board[1] = new Piece[8];
            board[2] = new Piece[8];
            board[3] = new Piece[8];
            board[4] = new Piece[8];
            board[5] = new Piece[8];
            board[6] = new Piece[8];
            board[7] = new Piece[8];

            board[0][0] = new King(Player.Black);
            board[1][2] = new King(Player.White);
            board[2][1] = new Queen(Player.White);
            data.WhoseTurn = Player.White;
            data.Board = board;

            ChessGame game = new ChessGame(data);
            game.MakeMove(move, false);
            if (game.IsCheckmated(Player.Black))
            {
                throw new NullReferenceException();
            }
        }
    }
}
