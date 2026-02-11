using ChessChallenge.API;
using System;
using System.Diagnostics;

public class MyBot : IChessBot
{

    private ulong nodeCount; // counter of explored nodes.
    private const float MAX = 1000000;

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

    public Move Think(Board board, Timer timer)
    {
        return Solve(board, timer);
    }

    public static float Evaluate(Board B)
    {
        int whiteMaterial = CountMaterial(B, true);
        int blackMaterial = CountMaterial(B, false);
        int eval = whiteMaterial - blackMaterial;

        return B.IsWhiteToMove ? eval : -eval;
    }

    public static int CountMaterial(Board B, bool isWhite)
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

    public Move Solve(Board B, Timer T)
    {
        nodeCount = 0;
        Move bestMove = default;
        Move previousBestMove = default;
        int maxDepth = 40; // large cap, time will stop us
        for (int depth = 1; depth <= maxDepth; depth++)
        {
            float bestScoreThisIteration = -MAX;
            Move bestMoveThisIteration = default;
            Move[] moves = B.GetLegalMoves();
            if (previousBestMove != default)
                for (int i = 0; i < moves.Length; i++)
                    if (moves[i] == previousBestMove)
                    {
                        (moves[0], moves[i]) = (moves[i], moves[0]);
                        break;
                    }
            OrderMoves(moves, B);
            foreach (Move move in moves)
            {
                B.MakeMove(move);
                float score = -Negamax(B, -MAX, MAX, depth - 1);
                B.UndoMove(move);

                if (score > bestScoreThisIteration)
                {
                    bestScoreThisIteration = score;
                    bestMoveThisIteration = move;
                }
            }

            bestMove = bestMoveThisIteration;
            previousBestMove = bestMoveThisIteration;

            // Stop if we are low on time
            if (T.MillisecondsElapsedThisTurn > 500)
                break;

            Console.WriteLine($"Depth {depth} done. Best: {bestMove} Score: {bestScoreThisIteration}");
        }
        return bestMove;
    }

    public float Negamax(Board B, float alpha, float beta, int depth)
    {
        Trace.Assert(alpha < beta);
        nodeCount++; // increment counter of explored nodes

        if (B.IsDraw()) // check for draw game
            return 0;
        else if (B.IsInCheckmate())
            return -MAX + B.PlyCount;
        else if (depth == 0)
            return SearchAllCaptures(B, alpha, beta);

        // Check extension: if we are in check, we should search deeper. More info: https://www.chessprogramming.org/Check_Extensions
        bool inCheck = B.IsInCheck();
        if (inCheck)
            depth++;

        Move[] moves = B.GetLegalMoves();
        OrderMoves(moves, B);

        foreach (Move move in moves) // compute the score of all possible next move and keep the best one
        {
            B.MakeMove(move);               // It's opponent turn in P2 position after current player plays x column.
            float score = -Negamax(B, -beta, -alpha, depth - 1); // explore opponent's score within [-beta;-alpha] windows:
            B.UndoMove(move); // no need to have good precision for score better than beta (opponent's score worse than -beta)
                                                        // no need to check for score worse than alpha (opponent's score worse better than -alpha)
            if (score >= beta) // prune the exploration if we find a possible move better than what we were looking for.
                return score;
            if (score > alpha) // reduce the [alpha;beta] window for next exploration, as we only
                alpha = score;  // need to search for a position that is better than the best so far.
        }
        return alpha;
    }

    public static float SearchAllCaptures(Board B, float alpha, float beta)
    {
        float eval =  Evaluate(B);
        if (eval + 900 < alpha)
            return alpha; //unsure
        if (eval >= beta)
            return beta;
        alpha = Math.Max(alpha, eval);

        Move[] moves = B.GetLegalMoves(true);
        OrderMoves(moves, B);
        foreach (Move move in moves)
        {
            if ((int)move.CapturePieceType < (int)move.MovePieceType)
                continue; // skip bad trades
            B.MakeMove(move);
            eval = -SearchAllCaptures(B, -beta, -alpha);
            B.UndoMove(move);

            if (eval >= beta)
                return beta;
            alpha = Math.Max(alpha, eval);
        }

        return alpha;
    }

    public static void OrderMoves(Move[] moves, Board B)
    {
        int[] scores = new int[moves.Length];
        for (int i = 0; i < moves.Length; i++)
        {
            int score = 0;
            Move move = moves[i];
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
        Sort(moves, scores);
    }

    public static int PieceScopeImprovement(Move move, Board B)
    {
        return move.MovePieceType switch
        {
            PieceType.Pawn => 0,
            PieceType.Knight => BitboardHelper.GetNumberOfSetBits(BitboardHelper.GetKnightAttacks(move.TargetSquare)) - BitboardHelper.GetNumberOfSetBits(BitboardHelper.GetKnightAttacks(move.StartSquare)),
            _ => BitboardHelper.GetNumberOfSetBits(BitboardHelper.GetSliderAttacks(move.MovePieceType, move.TargetSquare, B)) - BitboardHelper.GetNumberOfSetBits(BitboardHelper.GetSliderAttacks(move.MovePieceType, move.StartSquare, B)),
        };
    }

    public static bool IsSquareAttackedByEnemyPawns(Square S, Board B)
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
    public static int DevelopPieces(Move move, Board B, int score)
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

    public static void Sort(Move[] moves, int[] scores)
    {
        // Sort the moves list based on scores
        for (int i = 0; i < moves.Length - 1; i++)
            for (int j = i + 1; j > 0; j--)
            {
                int swapIndex = j - 1;
                if (scores[swapIndex] < scores[j])
                {
                    (moves[j], moves[swapIndex]) = (moves[swapIndex], moves[j]);
                    (scores[j], scores[swapIndex]) = (scores[swapIndex], scores[j]);
                }
            }
    }
}