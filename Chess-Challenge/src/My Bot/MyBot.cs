using ChessChallenge.API;
using System;
using System.Diagnostics;

public class MyBot : IChessBot
{

    private ulong nodeCount; // counter of explored nodes.
    private const int MAX = 1000000;

    public enum PieceValue
    {
        None,
        Pawn = 100,
        Knight = 290,
        Bishop = 310,
        Rook = 500,
        Queen = 950,
        King = 10000
    }

    const int TT_SIZE = 1 << 21; // 2M entries

    struct TTEntry
    {
        public ulong Key;
        public int Score;
        public int Depth;
        public byte Flag;     // 0 = exact, 1 = lower bound, 2 = upper bound
        public Move BestMove;
    }

    TTEntry[] TT = new TTEntry[TT_SIZE];

    static int TTIndex(ulong key) => (int)(key & (TT_SIZE - 1));

    public Move Think(Board board, Timer timer)
    {
        return Solve(board, timer);
    }

    static int Evaluate(Board B)
    {
        int whiteMaterial = CountMaterial(B, true);
        int blackMaterial = CountMaterial(B, false);
        int eval = whiteMaterial - blackMaterial;

        return B.IsWhiteToMove ? eval : -eval;
    }

    static int CountMaterial(Board B, bool isWhite)
    {
        int material = 0; // Assume there can be 0 kings for custom positions
        material += B.GetPieceList(PieceType.Pawn, isWhite).Count * ((int)PieceValue.Pawn);
        material += B.GetPieceList(PieceType.Knight, isWhite).Count * ((int)PieceValue.Knight);
        material += B.GetPieceList(PieceType.Bishop, isWhite).Count * ((int)PieceValue.Bishop);
        material += B.GetPieceList(PieceType.Rook, isWhite).Count * ((int)PieceValue.Rook);
        material += B.GetPieceList(PieceType.Queen, isWhite).Count * ((int)PieceValue.Queen);
        material += B.GetPieceList(PieceType.King, isWhite).Count * ((int)PieceValue.King);
        return material;
    }

    Move Solve(Board B, Timer T)
    {
        nodeCount = 0;
        Move bestMove = default;
        int previousScore = 0;
        int maxDepth = 40; // large cap, time will stop us
        for (int depth = 1; depth <= maxDepth; depth++)
        {
            int window = 50;
            int score = 0;
            Move bestMoveThisIteration = default;
            for (int tries = 0; tries < 5; tries++)
            {
                int alpha = previousScore - window;
                int beta = previousScore + window;

                score = SearchRoot(B, depth, alpha, beta, out bestMoveThisIteration);

                if (score > alpha && score < beta)
                    break;

                window *= 2;

                if (tries == 4)
                    score = SearchRoot(B, depth, -MAX, MAX, out bestMoveThisIteration);
            }

            previousScore = score;
            bestMove = bestMoveThisIteration;

            // Stop if we are low on time
            if (T.MillisecondsRemaining < 500 && T.MillisecondsElapsedThisTurn > 10)
                break;

            // Max per move
            if (T.MillisecondsElapsedThisTurn > 500)
                break;

            Console.WriteLine($"Depth {depth} done. Best: {bestMove} Score: {score}");
        }
        return bestMove;
    }

    int SearchRoot(Board B, int depth, int alpha, int beta, out Move bestMove)
    {
        ulong key = B.ZobristKey;
        TTEntry entry = TT[TTIndex(key)];
        Move ttMove = entry.Key == key ? entry.BestMove : default;

        Move[] moves = B.GetLegalMoves();
        OrderMoves(moves, B, ttMove);

        bestMove = default;

        foreach (Move move in moves)
        {
            B.MakeMove(move);
            int score = -Negamax(B, -beta, -alpha, depth - 1);
            B.UndoMove(move);

            if (score >= beta)
            {
                bestMove = move;
                return score;
            }

            if (score > alpha)
            {
                alpha = score;
                bestMove = move;
            }
        }

        return alpha;
    }

    int Negamax(Board B, int alpha, int beta, int depth)
    {
        // 1. Assertions / bookkeeping
        Trace.Assert(alpha < beta);
        nodeCount++; // increment counter of explored nodes

        // 2. Terminal checks
        if (B.IsDraw()) // check for draw game
            return 0;
        else if (B.IsInCheckmate())
            return -MAX + B.PlyCount;
        else if (depth == 0)
            return SearchAllCaptures(B, alpha, beta);

        // Check extension: if we are in check, we should search deeper. More info: https://www.chessprogramming.org/Check_Extensions
        bool inCheck = B.IsInCheck();
        if (inCheck) // This can cause search explosion in tactical positions. TODO: Improve this
            depth++;

        // 3. TT Lookup
        ulong key = B.ZobristKey;
        int ttIndex = TTIndex(key);
        TTEntry entry = TT[ttIndex];

        if (entry.Key == key && entry.Depth >= depth)
        {
            if (entry.Flag == 0) // exact
                return entry.Score;

            if (entry.Flag == 1 && entry.Score >= beta) // lower bound
                return entry.Score;

            if (entry.Flag == 2 && entry.Score <= alpha) // upper bound
                return entry.Score;
        }

        // 4. Depth == 0 → quiescence

        // TT

        int originalAlpha = alpha;
        Move bestMove = default;

        // 5.Move generation
        Move[] moves = B.GetLegalMoves();

        OrderMoves(moves, B, entry.BestMove);

        // 6. Search loop
        foreach (Move move in moves) // compute the score of all possible next move and keep the best one
        {
            B.MakeMove(move);               // It's opponent turn in P2 position after current player plays x column.
            int score = -Negamax(B, -beta, -alpha, depth - 1); // explore opponent's score within [-beta;-alpha] windows:
            B.UndoMove(move); // no need to have good precision for score better than beta (opponent's score worse than -beta)
                                                        // no need to check for score worse than alpha (opponent's score worse better than -alpha)
            if (score >= beta) // prune the exploration if we find a possible move better than what we were looking for.
            {
                // Store LOWERBOUND before returning
                if (TT[ttIndex].Key != key || depth >= TT[ttIndex].Depth)
                    TT[ttIndex] = new TTEntry
                    {
                        Key = key,
                        Score = score,
                        Depth = depth,
                        Flag = 1, // LOWERBOUND
                        BestMove = move
                    };
                return score;
            }
            if (score > alpha) // reduce the [alpha;beta] window for next exploration, as we only
                (alpha, bestMove) = (score, move);  // need to search for a position that is better than the best so far.
        }
        // 7.Store TT entry
        // Decide TT flag
        byte flag;

        if (alpha >= beta)
            flag = 1;          // LOWERBOUND
        else if (alpha <= originalAlpha)
            flag = 2;          // UPPERBOUND
        else
            flag = 0;          // EXACT

        TT[ttIndex] = new TTEntry
        {
            Key = key,
            Score = alpha,
            Depth = depth,
            Flag = flag,
            BestMove = bestMove
        };

        // 8. Return score
        return alpha;
    }

    static int SearchAllCaptures(Board B, int alpha, int beta)
    {
        int eval =  Evaluate(B);
        if (eval >= beta)
            return beta; // fail soft
        alpha = Math.Max(alpha, eval);

        Move[] moves = B.GetLegalMoves(true);
        OrderMoves(moves, B);
        foreach (Move move in moves)
        {
            if ((int)move.CapturePieceType < (int)move.MovePieceType) // TODO: Remove this with better SEE (Static Exchange Evaluation)
                continue; // skip bad trades
            B.MakeMove(move);
            eval = -SearchAllCaptures(B, -beta, -alpha);
            B.UndoMove(move);

            if (eval >= beta)
                return beta; // fail soft
            alpha = Math.Max(alpha, eval);
        }

        return alpha;
    }

    static void OrderMoves(Move[] moves, Board B, Move ttMove = default)
    {
        int[] scores = new int[moves.Length];
        for (int i = 0; i < moves.Length; i++)
        {
            int score = 0;
            Move move = moves[i];
            if (move == ttMove)
                score += 1000000;
            if (move.IsPromotion)
                score += (int)(PieceValue)move.PromotionPieceType;
            if (move.IsCapture) // Prioritise big gains captures
            {
                int victim = (int)(PieceValue)move.CapturePieceType;
                int attacker = (int)(PieceValue)move.MovePieceType;
                score += 10 * victim - attacker; // MVV-LVA
            } else
                score = DevelopPieces(move, B, score);
            // Penalize moving pieces to a square attacked by a pawn
            if (IsSquareAttackedByEnemyPawns(move.TargetSquare,B))
                score -= (int)(PieceValue)move.MovePieceType;
            scores[i] = score;
        }
        Array.Sort(scores, moves);
        Array.Reverse(moves);
    }

    static int PieceScopeImprovement(Move move, Board B)
    {
        return move.MovePieceType switch
        {
            PieceType.Pawn => 0,
            PieceType.Knight => BitboardHelper.GetNumberOfSetBits(BitboardHelper.GetKnightAttacks(move.TargetSquare)) - BitboardHelper.GetNumberOfSetBits(BitboardHelper.GetKnightAttacks(move.StartSquare)),
            _ => BitboardHelper.GetNumberOfSetBits(BitboardHelper.GetSliderAttacks(move.MovePieceType, move.TargetSquare, B)) - BitboardHelper.GetNumberOfSetBits(BitboardHelper.GetSliderAttacks(move.MovePieceType, move.StartSquare, B)),
        };
    }

    static bool IsSquareAttackedByEnemyPawns(Square S, Board B)
    {
        ulong ourPawnMoveAttacks = BitboardHelper.GetPawnAttacks(S, B.IsWhiteToMove);
        ulong enemyPawns = B.GetPieceBitboard(PieceType.Pawn, !B.IsWhiteToMove);
        return (ourPawnMoveAttacks & enemyPawns) != 0;
    }

    /// <summary>
    /// Look to improve the scope of pieces
    /// </summary>
    /// <param name="moves"></param>
    /// <returns></returns>
    static int DevelopPieces(Move move, Board B, int score)
    {
        if (move.MovePieceType == PieceType.Knight)
            score += PieceScopeImprovement(move, B) * 2;
        else if (move.MovePieceType == PieceType.Bishop)
            score += PieceScopeImprovement(move, B) / 5;
        else if (move.MovePieceType == PieceType.Rook)
            score += PieceScopeImprovement(move, B);
        else if (move.MovePieceType == PieceType.Queen)
            score += PieceScopeImprovement(move, B) / 10;
        else if (move.MovePieceType == PieceType.Pawn && Math.Abs(move.StartSquare.Rank - move.TargetSquare.Rank) == 2)
            if ((move.TargetSquare.File == 3 || move.TargetSquare.File == 4) && !IsSquareAttackedByEnemyPawns(move.TargetSquare, B))
                score += 20;
        return score;
    }
}