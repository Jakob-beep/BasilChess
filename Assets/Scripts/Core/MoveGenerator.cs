namespace Chess
{
    using System.Collections.Generic;
    using static PrecomputedMoveData;
    using static BoardRepresentation;

    public class MoveGenerator
    {

        public enum PromotionMode { All, QueenOnly, QueenAndKnight }

        public PromotionMode promotionsToGenerate = PromotionMode.All;

        // ---- Instance variables ----
        List<Move> moves;
        bool isWhiteToMove;
        int friendlyColour;
        int opponentColour;
        int friendlyKingSquare;
        int friendlyColourIndex;
        int opponentColourIndex;

        bool inCheck;
        bool pinsExistInPosition;
        ulong checkRayBitmask;
        List<ulong> pinRayBitmask;
        ulong opponentKnightAttacks;
        ulong opponentAttackMapNoPawns;
        public ulong opponentAttackMap;
        public ulong opponentPawnAttackMap;
        ulong opponentSlidingAttackMap;
        ulong opponentBishopAttackMap;
        ulong[] bishopCheckRays;
        ulong[] bishopPinRays;

        ulong totalPinMask;
        ulong totalCheckMask;

        bool conventionalDoubleCheck;

        bool genQuiets;
        Board board;

        // Generates list of legal moves in current position.
        // Quiet moves (non captures) can optionally be excluded. This is used in quiescence search.
        public List<Move> GenerateMoves(Board board, bool includeQuietMoves = true)
        {
            this.board = board;
            genQuiets = includeQuietMoves;
            Init();

            CalculateAttackData();
            GenerateKingMoves();


            //In conventional double check, only king can move!
            if (conventionalDoubleCheck)
            {
                return moves;
            }

            GenerateSlidingMoves();
            GenerateKnightMoves();
            GeneratePawnMoves();

            return moves;
        }

        // Note, this will only return correct value after GenerateMoves() has been called in the current position
        public bool InCheck()
        {
            return inCheck;
        }

        void Init()
        {
            moves = new List<Move>(64);
            inCheck = false;
            pinsExistInPosition = false;
            checkRayBitmask = 0;

            isWhiteToMove = board.ColourToMove == Piece.White;
            friendlyColour = board.ColourToMove;
            opponentColour = board.OpponentColour;
            friendlyKingSquare = board.KingSquare[board.ColourToMoveIndex];
            friendlyColourIndex = (board.WhiteToMove) ? Board.WhiteIndex : Board.BlackIndex;
            opponentColourIndex = 1 - friendlyColourIndex;
        }

        void GenerateKingMoves()
        {
            for (int i = 0; i < kingMoves[friendlyKingSquare].Length; i++)
            {
                int targetSquare = kingMoves[friendlyKingSquare][i];
                int pieceOnTargetSquare = board.Square[targetSquare];

                // Skip squares occupied by friendly pieces
                if (Piece.IsColour(pieceOnTargetSquare, friendlyColour))
                {
                    continue;
                }

                bool isCapture = Piece.IsColour(pieceOnTargetSquare, opponentColour);
                if (!isCapture && !genQuiets)
                {
                    continue;
                }

                // Safe for king to move to this square
                if (!SquareIsAttacked(targetSquare))
                {
                    moves.Add(new Move(friendlyKingSquare, targetSquare));

                    // Castling:
                    if (!inCheck && !isCapture)
                    {
                        // Castle kingside
                        if ((targetSquare == f1 || targetSquare == f8) && HasKingsideCastleRight)
                        {
                            int castleKingsideSquare = targetSquare + 1;
                            if (board.Square[castleKingsideSquare] == Piece.None)
                            {
                                if (!SquareIsAttacked(castleKingsideSquare))
                                {
                                    moves.Add(new Move(friendlyKingSquare, castleKingsideSquare, Move.Flag.Castling));
                                }
                            }
                        }
                        // Castle queenside
                        else if ((targetSquare == d1 || targetSquare == d8) && HasQueensideCastleRight)
                        {
                            int castleQueensideSquare = targetSquare - 1;
                            if (board.Square[castleQueensideSquare] == Piece.None && board.Square[castleQueensideSquare - 1] == Piece.None)
                            {
                                if (!SquareIsAttacked(castleQueensideSquare))
                                {
                                    moves.Add(new Move(friendlyKingSquare, castleQueensideSquare, Move.Flag.Castling));
                                }
                            }
                        }
                    }
                }
            }
        }

        void GenerateSlidingMoves()
        {
            PieceList rooks = board.rooks[friendlyColourIndex];
            for (int i = 0; i < rooks.Count; i++)
            {
                GenerateRookMoves(rooks[i]);
            }

            PieceList bishops = board.bishops[friendlyColourIndex];
            for (int i = 0; i < bishops.Count; i++)
            {
                GenerateBishopMoves(bishops[i]);
            }

            PieceList queens = board.queens[friendlyColourIndex];
            for (int i = 0; i < queens.Count; i++)
            {
                GenerateSlidingPieceMoves(queens[i], 0, 8);
            }

        }
        void GenerateBishopMoves(int startSquare)
        {
            ulong checkedSquares = 0; 
            bool pinned = IsPinned(startSquare);
            ulong targetSquareIsValid = 0;
            bool firstPin = true;
            //Knight can only move along pin(s) if pinned. Calculate valid target squares if pinned
            if (pinned)
            {
                for (int j = 0; j < pinRayBitmask.Count && firstPin; j++)
                {
                    if (((pinRayBitmask[j] >> startSquare) & 1) != 0)
                    {
                        targetSquareIsValid = pinRayBitmask[j];
                        firstPin = false;
                    }
                }
                for (int j = 0; j < bishopPinRays.Length; j++)
                {
                    if (((bishopPinRays[j] >> startSquare) & 1) != 0)
                    {
                        if (firstPin)
                        {
                            targetSquareIsValid = bishopPinRays[j];
                        }
                        else
                        {
                            targetSquareIsValid &= bishopPinRays[j];
                        }
                        firstPin = false;
                    }
                }

                //If no valid squares found, piece can't move
                if (targetSquareIsValid == 0)
                {
                    return;
                }
            }
            for (int diagonalDirectionIndex = 4; diagonalDirectionIndex < 8; diagonalDirectionIndex++)
            {
                int currentDiagonalDirOffset = directionOffsets[diagonalDirectionIndex];


                for (int n = 0; n < numSquaresToEdge[startSquare][diagonalDirectionIndex]; n++)
                {
                    int intermediateSquare = startSquare + currentDiagonalDirOffset * (n + 1);
                    int intermediateSquarePiece = board.Square[intermediateSquare];

                    // Blocked by piece, so stop looking in this direction
                    if (intermediateSquarePiece != Piece.None)
                    {
                        break;
                    }
                    for (int cardinalDirecitonIndex = 0; cardinalDirecitonIndex < 4; cardinalDirecitonIndex++)
                    {
                        int currentCardinalDirOffset = directionOffsets[cardinalDirecitonIndex];
                        int nsq = numSquaresToEdge[intermediateSquare][cardinalDirecitonIndex];
                        for (int i = 0; i <= nsq; i++)
                        {
                            int targetSquare = intermediateSquare + currentCardinalDirOffset * i;
                            int targetSquarePiece = board.Square[targetSquare];

                            // Blocked by friendly piece, so stop looking in this direction
                            if (Piece.IsColour(targetSquarePiece, friendlyColour))
                            {
                                break;
                            }
                            bool isCapture = targetSquarePiece != Piece.None;
                            if ((genQuiets || isCapture) && (!pinned || (((targetSquareIsValid >> targetSquare) & 1) != 0)) && (!inCheck || SquareIsInAllCheckRays(targetSquare)))
                            {
                                if ((checkedSquares & (1ul << targetSquare)) == 0)
                                {
                                    moves.Add(new Move(startSquare, targetSquare));
                                    checkedSquares |= 1ul << targetSquare;
                                }
                            }
                            // If square not empty, can't move any further in this direction
                            if (isCapture)
                            {
                                break;
                            }
                        }
                    }
                }
            }
        }
        void GenerateRookMoves(int startSquare)
        {
            bool pinned = IsPinned(startSquare);
            ulong targetSquareIsValid = 0;
            bool firstPin = true;
            //Piece can only move along pin(s) if pinned. Calculate valid target squares if pinned
            if (pinned)
            {
                for (int j = 0; j < pinRayBitmask.Count && firstPin; j++)
                {
                    if (((pinRayBitmask[j] >> startSquare) & 1) != 0)
                    {
                        targetSquareIsValid = pinRayBitmask[j];
                        firstPin = false;
                    }
                }
                for (int j = 0; j < bishopPinRays.Length; j++)
                {
                    if (((bishopPinRays[j] >> startSquare) & 1) != 0)
                    {
                        if (firstPin)
                        {
                            targetSquareIsValid = bishopPinRays[j];
                        }
                        else
                        {
                            targetSquareIsValid &= bishopPinRays[j];
                        }
                        firstPin = false;
                    }
                }

                //If no valid squares found, piece can't move
                if (targetSquareIsValid == 0)
                {
                    return;
                }
            }
            for (int directionIndex = 0; directionIndex < 2; directionIndex++)
            {
                int currentDirOffset = directionOffsets[directionIndex];

                int nsq = numSquaresToEdge[startSquare][directionIndex];

                for (int n = 0; n < nsq; n++)
                {
                    int targetSquare = startSquare + currentDirOffset * (n + 1);
                    int targetSquarePiece = board.Square[targetSquare];

                    // Blocked by friendly piece, so stop looking in this direction
                    if (Piece.IsColour(targetSquarePiece, friendlyColour))
                    {
                        break;
                    }
                    bool isCapture = targetSquarePiece != Piece.None;

                    if ((genQuiets || isCapture) && (!pinned || (((targetSquareIsValid >> targetSquare) & 1) != 0)) && (!inCheck || SquareIsInAllCheckRays(targetSquare)))
                    {
                        moves.Add(new Move(startSquare, targetSquare));
                    }
                    // If square not empty, can't move any further in this direction
                    // Also, if this move blocked a check, further moves won't block the check
                    if (isCapture)
                    {
                        break;
                    }
                }
            }
            // West & East
            // Preallocate array for positional check
            int[] checkedPositions = { -1, -1, -1, -1, -1, -1, -1, -1 };
            int nCheckedPositions = 0;
            for (int directionIndex = 2; directionIndex < 4 && nCheckedPositions < 7; directionIndex++)
            {

                //Prewrap


                int currentDirOffset = directionOffsets[directionIndex];
                int nsq = numSquaresToEdge[startSquare][directionIndex];
                bool wrap = true;
                for (int n = 0; n < nsq && nCheckedPositions < 7; n++)
                {
                    int targetSquare = startSquare + currentDirOffset * (n + 1);
                    int targetSquarePiece = board.Square[targetSquare];

                    // Blocked by friendly piece, so stop looking in this direction
                    if (Piece.IsColour(targetSquarePiece, friendlyColour))
                    {
                        wrap = false;
                        break;
                    }
                    bool isCapture = targetSquarePiece != Piece.None;

                    if ((genQuiets || isCapture) && (!pinned || (((targetSquareIsValid >> targetSquare) & 1) != 0)) && (System.Array.IndexOf(checkedPositions, targetSquare) == -1) && (!inCheck || SquareIsInAllCheckRays(targetSquare)))
                    {
                        checkedPositions[nCheckedPositions] = targetSquare;
                        nCheckedPositions++;
                        moves.Add(new Move(startSquare, targetSquare));
                    }
                    // If square not empty, can't move any further in this direction
                    if (isCapture)
                    {
                        wrap = false;
                        break;
                    }
                }

                // Wrap
                if (wrap)
                {
                    int wrapsquare = startSquare + currentDirOffset * (nsq + 1) + directionOffsets[directionIndex - 2];
                    for (int n = 0; n < 7 && nCheckedPositions < 7; n++)
                    {
                        int targetSquare = wrapsquare + currentDirOffset * n;
                        int targetSquarePiece = board.Square[targetSquare];
                        // Blocked by friendly piece, so stop looking in this direction
                        if (Piece.IsColour(targetSquarePiece, friendlyColour))
                        {
                            break;
                        }
                        bool isCapture = targetSquarePiece != Piece.None;

                        if ((genQuiets || isCapture) && (!pinned || (((targetSquareIsValid >> targetSquare) & 1) != 0)) && (System.Array.IndexOf(checkedPositions, targetSquare) == -1) && (!inCheck || SquareIsInAllCheckRays(targetSquare)))
                        {
                            checkedPositions[nCheckedPositions] = targetSquare;
                            nCheckedPositions++;
                            moves.Add(new Move(startSquare, targetSquare));
                        }
                        // If square not empty, can't move any further in this direction
                        if (isCapture)
                        {
                            break;
                        }
                    }
                }
            }

        }
        void GenerateSlidingPieceMoves(int startSquare, int startDirIndex, int endDirIndex)
        {
            bool pinned = IsPinned(startSquare);
            ulong targetSquareIsValid = 0;
            bool firstPin = true;
            //Piece can only move along pin(s) if pinned. Calculate valid target squares if pinned
            if (pinned)
            {
                for (int j = 0; j < pinRayBitmask.Count && firstPin; j++)
                {
                    if (((pinRayBitmask[j] >> startSquare) & 1) != 0)
                    {
                        targetSquareIsValid = pinRayBitmask[j];
                        firstPin = false;
                    }
                }
                for (int j = 0; j < bishopPinRays.Length; j++)
                {
                    if (((bishopPinRays[j] >> startSquare) & 1) != 0)
                    {
                        if (firstPin)
                        {
                            targetSquareIsValid = bishopPinRays[j];
                        }
                        else
                        {
                            targetSquareIsValid &= bishopPinRays[j];
                        }
                        firstPin = false;
                    }
                }

                //If no valid squares found, piece can't move
                if (targetSquareIsValid == 0)
                {
                    return;
                }
            }

            for (int directionIndex = startDirIndex; directionIndex < endDirIndex; directionIndex++)
            {
                int currentDirOffset = directionOffsets[directionIndex];

                int nsq = numSquaresToEdge[startSquare][directionIndex];

                for (int n = 0; n < nsq; n++)
                {
                    int targetSquare = startSquare + currentDirOffset * (n + 1);
                    int targetSquarePiece = board.Square[targetSquare];

                    // Blocked by friendly piece, so stop looking in this direction
                    if (Piece.IsColour(targetSquarePiece, friendlyColour))
                    {
                        break;
                    }
                    bool isCapture = targetSquarePiece != Piece.None;

                    if ((genQuiets || isCapture) && (!pinned || (((targetSquareIsValid >> targetSquare) & 1) != 0)) && (!inCheck || SquareIsInAllCheckRays(targetSquare)))
                    {
                        moves.Add(new Move(startSquare, targetSquare));
                    }
                    // If square not empty, can't move any further in this direction
                    // Also, if this move blocked a check, further moves won't block the check
                    if (isCapture)
                    {
                        break;
                    }
                }
            }
        }

        void GenerateKnightMoves()
        {
            PieceList myKnights = board.knights[friendlyColourIndex];

            for (int i = 0; i < myKnights.Count; i++)
            {
                int startSquare = myKnights[i];

                bool pinned = IsPinned(startSquare);
                ulong targetSquareIsValid = 0;
                bool firstPin = true;
                //Knight can only move along pin(s) if pinned. Calculate valid target squares if pinned
                if (pinned)
                {
                    for (int j = 0; j < pinRayBitmask.Count && firstPin; j++)
                    {
                        if (((pinRayBitmask[j] >> startSquare) & 1) != 0)
                        {
                            targetSquareIsValid = pinRayBitmask[j];
                            firstPin = false;
                        }
                    }
                    for (int j = 0; j < bishopPinRays.Length; j++)
                    {
                        if (((bishopPinRays[j] >> startSquare) & 1) != 0)
                        {
                            if (firstPin)
                            {
                                targetSquareIsValid = bishopPinRays[j];
                            }
                            else
                            {
                                targetSquareIsValid &= bishopPinRays[j];
                            }
                            firstPin = false;
                        }
                    }

                    //If no valid squares found, piece can't move
                    if (targetSquareIsValid == 0)
                    {
                        continue;
                    }
                }

                for (int knightMoveIndex = 0; knightMoveIndex < knightMoves[startSquare].Length; knightMoveIndex++)
                {
                    int targetSquare = knightMoves[startSquare][knightMoveIndex];
                    if (!pinned || ((targetSquareIsValid >> targetSquare) & 1) != 0)
                    {
                        int targetSquarePiece = board.Square[targetSquare];
                        bool isCapture = Piece.IsColour(targetSquarePiece, opponentColour);
                        if (genQuiets || isCapture)
                        {
                            // Skip if square contains friendly piece, or if in check and knight is not interposing/capturing checking piece(s)
                            if (Piece.IsColour(targetSquarePiece, friendlyColour) || (inCheck && !SquareIsInAllCheckRays(targetSquare)))
                            {
                                continue;
                            }
                            moves.Add(new Move(startSquare, targetSquare));
                        }
                    }
                }
            }
        }

        void GeneratePawnMoves()
        {
            PieceList myPawns = board.pawns[friendlyColourIndex];
            int[] pawnOffset = { (friendlyColour == Piece.White) ? 7 : -9, (friendlyColour == Piece.White) ? 9 : -7 };
            byte[] pawnMoveDirection = { (byte)((friendlyColour == Piece.White) ? 4 : 7), (byte)((friendlyColour == Piece.White) ? 6 : 5) };
            int startRank = (board.WhiteToMove) ? 1 : 6;
            int finalRankBeforePromotion = (board.WhiteToMove) ? 6 : 1;




            int enPassantFile = ((int)(board.currentGameState >> 4) & 15) - 1;
            int enPassantSquare = -1;
            if (enPassantFile != -1)
            {
                enPassantSquare = 8 * ((board.WhiteToMove) ? 5 : 2) + enPassantFile;
            }

            for (int i = 0; i < myPawns.Count; i++)
            {
                int startSquare = myPawns[i];

                bool pinned = IsPinned(startSquare);
                ulong targetSquareIsValid = 0;
                bool firstPin = true;
                //Knight can only move along pin(s) if pinned. Calculate valid target squares if pinned
                if (pinned)
                {
                    for (int j = 0; j < pinRayBitmask.Count && firstPin; j++)
                    {
                        if (((pinRayBitmask[j] >> startSquare) & 1) != 0)
                        {
                            targetSquareIsValid = pinRayBitmask[j];
                            firstPin = false;
                        }
                    }
                    for (int j = 0; j < bishopPinRays.Length; j++)
                    {
                        if (((bishopPinRays[j] >> startSquare) & 1) != 0)
                        {
                            if (firstPin)
                            {
                                targetSquareIsValid = bishopPinRays[j];
                            }
                            else
                            {
                                targetSquareIsValid &= bishopPinRays[j];
                            }
                            firstPin = false;
                        }
                    }

                    //If no valid squares found, piece can't move
                    if (targetSquareIsValid == 0)
                    {
                        continue;
                    }
                }

                int rank = RankIndex(startSquare);
                bool oneStepFromPromotion = rank == finalRankBeforePromotion;

                if (genQuiets)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        //Check if square exists
                        if (numSquaresToEdge[startSquare][pawnMoveDirection[j]] > 0)
                        {
                            int squareOneForward = startSquare + pawnOffset[j];

                            // Square ahead of pawn is empty: forward moves
                            if (board.Square[squareOneForward] == Piece.None)
                            {
                                // Pawn not pinned, or is moving along line of pin
                                if (!pinned || ((targetSquareIsValid >> squareOneForward) & 1) != 0)
                                {
                                    // Not in check, or pawn is interposing checking piece
                                    if (!inCheck || SquareIsInAllCheckRays(squareOneForward))
                                    {
                                        if (oneStepFromPromotion)
                                        {
                                            MakePromotionMoves(startSquare, squareOneForward);
                                        }
                                        else
                                        {
                                            moves.Add(new Move(startSquare, squareOneForward));
                                        }
                                    }

                                    // Is on starting square (so can move two forward if not blocked)
                                    if (rank == startRank)
                                    {
                                        //Check if square exists
                                        if (numSquaresToEdge[squareOneForward][pawnMoveDirection[j]] > 0)
                                        {
                                            int squareTwoForward = squareOneForward + pawnOffset[j];
                                            if (board.Square[squareTwoForward] == Piece.None)
                                            {
                                                // Not in check, or pawn is interposing checking piece
                                                // And not pinned, or moving along pins
                                                if ((!pinned || ((targetSquareIsValid >> squareOneForward) & 1) != 0) && (!inCheck || SquareIsInAllCheckRays(squareTwoForward)))
                                                {
                                                    if (j == 0)
                                                    {
                                                        moves.Add(new Move(startSquare, squareTwoForward, Move.Flag.PawnTwoWest));
                                                    }
                                                    else
                                                    {
                                                        moves.Add(new Move(startSquare, squareTwoForward, Move.Flag.PawnTwoEast));
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Pawn captures.
                for (int j = 0; j < 1; j++)
                {
                    // move in direction friendly pawns attack to get square from which enemy pawn would attack
                    int pawnCaptureDir = directionOffsets[pawnAttackDirections[friendlyColourIndex][j]];
                    int targetSquare = startSquare + pawnCaptureDir;
                    int targetPiece = board.Square[targetSquare];

                    // If piece is pinned, and the square it wants to move to is not on same line as the pin, then skip this direction
                    if (pinned && ((targetSquareIsValid >> targetSquare) == 0))
                    {
                        continue;
                    }

                    // Regular capture
                    if (Piece.IsColour(targetPiece, opponentColour))
                    {
                        // If in check, and piece is not capturing/interposing the checking piece, then skip to next square
                        if (inCheck && !SquareIsInAllCheckRays(targetSquare))
                        {
                            continue;
                        }
                        if (oneStepFromPromotion)
                        {
                            MakePromotionMoves(startSquare, targetSquare);
                        }
                        else
                        {
                            moves.Add(new Move(startSquare, targetSquare));
                        }
                    }

                    // Capture en-passant
                    if (targetSquare == enPassantSquare)
                    {
                        int epCapturedPawnSquare = targetSquare + ((board.WhiteToMove) ? (((board.currentGameState >> 14) & 1) == 1) ? -7 : -9 : (((board.currentGameState >> 14) & 1) == 1) ? 9 : 7);
                        if (!InCheckAfterEnPassant(startSquare, targetSquare, epCapturedPawnSquare))
                        {
                            if (((board.currentGameState >> 14) & 1) == 1)
                            {
                                moves.Add(new Move(startSquare, targetSquare, Move.Flag.EnPassantEast));
                            }
                            else
                            {
                                moves.Add(new Move(startSquare, targetSquare, Move.Flag.EnPassantWest));
                            }
                        }
                    }
                }
            }
        }

        void MakePromotionMoves(int fromSquare, int toSquare)
        {
            moves.Add(new Move(fromSquare, toSquare, Move.Flag.PromoteToQueen));
            if (promotionsToGenerate == PromotionMode.All)
            {
                moves.Add(new Move(fromSquare, toSquare, Move.Flag.PromoteToKnight));
                moves.Add(new Move(fromSquare, toSquare, Move.Flag.PromoteToRook));
                moves.Add(new Move(fromSquare, toSquare, Move.Flag.PromoteToBishop));
            }
            else if (promotionsToGenerate == PromotionMode.QueenAndKnight)
            {
                moves.Add(new Move(fromSquare, toSquare, Move.Flag.PromoteToKnight));
            }

        }

        bool IsMovingAlongRay(int rayDir, int startSquare, int targetSquare)
        {
            int moveDir = directionLookup[targetSquare - startSquare + 63];
            return (rayDir == moveDir || -rayDir == moveDir);
        }

        //bool IsMovingAlongRay (int directionOffset, int absRayOffset) {
        //return !((directionOffset == 1 || directionOffset == -1) && absRayOffset >= 7) && absRayOffset % directionOffset == 0;
        //}

        bool IsPinned(int square)
        {
            return pinsExistInPosition && ((totalPinMask >> square) & 1) != 0;
        }

        bool SquareIsInAllCheckRays(int square)
        {
            if (totalCheckMask != 0 && ((totalCheckMask >> square) & 1) == 0)
            {
                return false;
            }
            if (checkRayBitmask != 0 && ((checkRayBitmask >> square) & 1) == 0)
            {
                return false;
            }
            for (int i = 0; i < bishopCheckRays.Length; i++)
            {
                if (((bishopCheckRays[i] >> square) & 1) == 0)
                {
                    return false;
                }
            }
            return true;
        }

        bool HasKingsideCastleRight
        {
            get
            {
                int mask = (board.WhiteToMove) ? 1 : 4;
                return (board.currentGameState & mask) != 0;
            }
        }

        bool HasQueensideCastleRight
        {
            get
            {
                int mask = (board.WhiteToMove) ? 2 : 8;
                return (board.currentGameState & mask) != 0;
            }
        }

        void GenSlidingAttackMap()
        {
            opponentSlidingAttackMap = 0;
            opponentBishopAttackMap = 0;

            PieceList enemyRooks = board.rooks[opponentColourIndex];
            for (int i = 0; i < enemyRooks.Count; i++)
            {
                UpdateRookAttackPiece(enemyRooks[i]);
            }

            PieceList enemyQueens = board.queens[opponentColourIndex];
            for (int i = 0; i < enemyQueens.Count; i++)
            {
                UpdateSlidingAttackPiece(enemyQueens[i], 0, 8);
            }

            PieceList enemyBishops = board.bishops[opponentColourIndex];
            for (int i = 0; i < enemyBishops.Count; i++)
            {
                UpdateBishopAttackPiece(enemyBishops[i]);
            }
        }

        void UpdateBishopAttackPiece(int startSquare)
        {
            for (int diagonalDirectionIndex = 4; diagonalDirectionIndex < 8; diagonalDirectionIndex++)
            {
                int currentDiagonalDirOffset = directionOffsets[diagonalDirectionIndex];
                for (int n = 0; n < numSquaresToEdge[startSquare][diagonalDirectionIndex]; n++)
                {
                    int intermediateSquare = startSquare + currentDiagonalDirOffset * (n + 1);
                    int intermediateSquarePiece = board.Square[intermediateSquare];
                    if (intermediateSquarePiece != Piece.None && intermediateSquare != friendlyKingSquare)
                    {
                        break;
                    }

                    for (int cardinalDirectionIndex = 0; cardinalDirectionIndex < 4; cardinalDirectionIndex++)
                    {
                        int currentCardinalDirOffset = directionOffsets[cardinalDirectionIndex];
                        for (int i = 0; i < numSquaresToEdge[intermediateSquare][cardinalDirectionIndex]; i++)
                        {
                            int targetSquare = intermediateSquare + currentCardinalDirOffset * (i + 1);
                            int targetSquarePiece = board.Square[targetSquare];
                            opponentBishopAttackMap |= 1ul << targetSquare;
                            if (targetSquare != friendlyKingSquare)
                            {
                                if (targetSquarePiece != Piece.None)
                                {
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        void UpdateRookAttackPiece(int startSquare)
        {
            for (int directionIndex = 0; directionIndex < 4; directionIndex++)
            {

                int nsq = numSquaresToEdge[startSquare][directionIndex];
                int currentDirOffset = directionOffsets[directionIndex];

                bool noPieceFound = true;
                for (int n = 0; n < nsq; n++)
                {
                    int targetSquare = startSquare + currentDirOffset * (n + 1);
                    int targetSquarePiece = board.Square[targetSquare];
                    opponentSlidingAttackMap |= 1ul << targetSquare;
                    if (targetSquare != friendlyKingSquare)
                    {
                        if (targetSquarePiece != Piece.None)
                        {

                            noPieceFound = false;
                            break;
                        }
                    }
                }
                if (noPieceFound && directionIndex > 1)
                {
                    //Wrap

                    int wrapSquare = startSquare + (nsq + 1) * currentDirOffset + directionOffsets[directionIndex - 2];
                    for (int i = 0; i < 7 - nsq; i++)
                    {
                        int targetSquare = wrapSquare + currentDirOffset * i;
                        int targetSquarePiece = board.Square[targetSquare];
                        opponentSlidingAttackMap |= 1ul << targetSquare;
                        if (targetSquare != friendlyKingSquare)
                        {
                            if (targetSquarePiece != Piece.None)
                            {
                                break;
                            }
                        }
                    }
                }
            }
        }

        void UpdateSlidingAttackPiece(int startSquare, int startDirIndex, int endDirIndex)
        {

            for (int directionIndex = startDirIndex; directionIndex < endDirIndex; directionIndex++)
            {
                int currentDirOffset = directionOffsets[directionIndex];
                for (int n = 0; n < numSquaresToEdge[startSquare][directionIndex]; n++)
                {
                    int targetSquare = startSquare + currentDirOffset * (n + 1);
                    int targetSquarePiece = board.Square[targetSquare];
                    opponentSlidingAttackMap |= 1ul << targetSquare;
                    if (targetSquare != friendlyKingSquare)
                    {
                        if (targetSquarePiece != Piece.None)
                        {
                            break;
                        }
                    }
                }
            }
        }
        void GenBishopAttackRays()
        {
            PieceList enemyBishops = board.bishops[opponentColourIndex];
            ulong[] tmpChecks = new ulong[enemyBishops.Count * 4];
            int iChecks = 0;
            ulong[] tmpPins = new ulong[enemyBishops.Count * 4];
            int iPins = 0;
            for (int i = 0; i < enemyBishops.Count; i++)
            {
                bool[] isPin;
                ulong[] tmp = GenBishopAttackRay(enemyBishops[i], friendlyKingSquare, out isPin);
                for (int j = 0; j < 4; j++)
                {
                    if (tmp[j] != 0)
                    {
                        if (isPin[j])
                        {
                            tmpPins[iPins] = tmp[j];
                            iPins++;
                            pinsExistInPosition = true;
                        }
                        else
                        {
                            tmpChecks[iChecks] = tmp[j];
                            iChecks++;
                        }
                    }
                }
            }
            bishopCheckRays = new ulong[iChecks];
            bishopPinRays = new ulong[iPins];
            for (int i = 0; i < iPins; i++)
            {
                bishopPinRays[i] = tmpPins[i];
            }
            for (int i = 0; i < iChecks; i++)
            {
                bishopCheckRays[i] = tmpChecks[i];
            }

        }
        ulong[] GenBishopAttackRay(int startSquare, int targetSquare, out bool[] isPin)
        {
            isPin = new bool[] { false, false, false, false };
            ulong[] rays = new ulong[4];
            int dx = FileIndex(targetSquare) - FileIndex(startSquare);
            int dy = RankIndex(targetSquare) - RankIndex(startSquare);
            bool isDiagonal = dx == dy || dx == -dy;
            int currentSquare;
            int currentDirOffset;
            //Ray 0: move diagonally up and left / right, then cardinally up / down 
            //Ray 0 can't exist if King is diagonal, and dy > 0, or if dx = 0
            //Ray 1: move diagonally down and left / right, then cardinally up / down 
            //Ray 1 can't exist if King is diagonal, and dy < 0, or if dx = 0 
            //Ray 2: move diagonally left and up / down, then cardinally left / right 
            //Ray 2 can't exist if King is diagonal, and dx < 0, or if dy = 0 
            //Ray 3: move diagonally right and up / down, then cardinally left / right 
            //Ray 3 can't exist if King is diagonal, and dx > 0, or if dy = 0 
            bool[] rayExists = new bool[]
            {
                (!isDiagonal || dy < 0) && dx != 0,
                (!isDiagonal || dy > 0) && dx != 0,
                (!isDiagonal || dx > 0) && dy != 0,
                (!isDiagonal || dx < 0) && dy != 0
            };
            int[] diagonalDirOffsets = new int[4];
            if (dx > 0)
            {
                diagonalDirOffsets[0] = 9;
                diagonalDirOffsets[1] = -7;
            }
            else
            {
                diagonalDirOffsets[0] = 7;
                diagonalDirOffsets[1] = -9;
            }
            if (dy > 0)
            {
                diagonalDirOffsets[2] = 7;
                diagonalDirOffsets[3] = 9;
            }
            else
            {
                diagonalDirOffsets[2] = -9;
                diagonalDirOffsets[3] = -7;
            }
            int[] diagonalSteps = new int[]
            {
                dx > 0 ? dx : -dx,
                0,
                dy > 0 ? dy : -dy,
                0
            };
            diagonalSteps[1] = diagonalSteps[0];
            diagonalSteps[3] = diagonalSteps[2];
            int[] cardinalSteps = new int[]
            {
                dy > diagonalSteps[0] ? dy - diagonalSteps[0]  - 1 : diagonalSteps[0] - dy - 1,
                dy > -diagonalSteps[1] ? dy + diagonalSteps[1]  - 1 : -dy - diagonalSteps[1] - 1,
                dx > -diagonalSteps[2] ? dx + diagonalSteps[2]  - 1 : -dx - diagonalSteps[2] - 1,
                dx > diagonalSteps[3] ? dx - diagonalSteps[3]  - 1 : diagonalSteps[3] - dx - 1
            };
            int[] cardinalDirOffsets = new int[]
            {
                dy > diagonalSteps[0] ? 8 : -8,
                dy > -diagonalSteps[1] ? 8 : -8,
                dx > -diagonalSteps[2] ? 1 : -1,
                dx > diagonalSteps[3] ? 1 : -1
            };
            for (int n = 0; n < 4; n++)
            {
                if (rayExists[n])
                {
                    currentDirOffset = diagonalDirOffsets[n];
                    int diagonalTarget = diagonalSteps[n] * currentDirOffset + startSquare;
                    bool friendlyAlongRay = false;
                    currentSquare = startSquare;
                    if (diagonalTarget >= 0 && diagonalTarget < 64)
                    {
                        //Add bishop to ray
                        rays[n] = 1ul << currentSquare;
                        for (int i = 0; i < diagonalSteps[n]; i++)
                        {
                            currentSquare += currentDirOffset;
                            int currentPiece = board.Square[currentSquare];
                            if (currentPiece != Piece.None)
                            {
                                if (Piece.IsColour(currentPiece, friendlyColour))
                                {
                                    if (friendlyAlongRay)
                                    {
                                        //Second friendly we've encountered, therefore not a real attack
                                        rays[n] = 0;
                                        break;
                                    }
                                    else
                                    {
                                        friendlyAlongRay = true;
                                    }
                                }
                                else
                                {
                                    //Enemy -> not a real attack
                                    rays[n] = 0;
                                    break;
                                }
                            }
                            rays[n] |= 1ul << currentSquare;
                        }
                        if (rays[n] != 0)
                        {
                            //Diagonal move worked, generate cardinal move
                            currentDirOffset = cardinalDirOffsets[n];
                            for (int i = 0; i < cardinalSteps[n]; i++)
                            {
                                currentSquare += currentDirOffset;
                                int currentPiece = board.Square[currentSquare];
                                if (currentPiece != Piece.None)
                                {
                                    if (Piece.IsColour(currentPiece, friendlyColour))
                                    {
                                        if (friendlyAlongRay)
                                        {
                                            //Second friendly we've encountered, therefore not a real attack
                                            rays[n] = 0;
                                            break;
                                        }
                                        else
                                        {
                                            friendlyAlongRay = true;
                                        }
                                    }
                                    else
                                    {
                                        //Enemy -> not a real attack
                                        rays[n] = 0;
                                        break;
                                    }
                                }
                                rays[n] |= 1ul << currentSquare;
                            }
                        }
                    }
                    isPin[n] = friendlyAlongRay;
                }
            }
            return rays;
        }

        void CalculateAttackData()
        {
            pinRayBitmask = new List<ulong>();
            GenSlidingAttackMap();
            GenBishopAttackRays();
            totalCheckMask = 0;
            for (int i = 0; i < bishopCheckRays.Length; i++)
            {
                totalCheckMask |= bishopCheckRays[i];
            }
            totalPinMask = 0;
            for (int i = 0; i < bishopPinRays.Length; i++)
            {
                totalPinMask |= bishopPinRays[i];
            }
            // Search squares in all directions around friendly king for checks/pins by enemy sliding pieces (queen, (rook))
            int startDirIndex = 0;
            int endDirIndex = 8;


            if (board.queens[opponentColourIndex].Count == 0)
            {
                startDirIndex = (board.rooks[opponentColourIndex].Count > 0) ? 0 : 4;
                endDirIndex = (board.bishops[opponentColourIndex].Count > 0) ? 8 : 4;
            }

            for (int dir = startDirIndex; dir < endDirIndex; dir++)
            {
                bool isDiagonal = dir > 3;
                bool isHorizontal = dir > 1 && dir < 4;
                int n = numSquaresToEdge[friendlyKingSquare][dir];
                int directionOffset = directionOffsets[dir];
                bool isFriendlyPieceAlongRay = false;
                ulong rayMask = 0;
                //if king on edge square, rook wrapping might still be possible
                if (n == 0 && isHorizontal)
                {
                    //wrap
                    int squareIndex = friendlyKingSquare + directionOffsets[dir - 2];
                    for (int j = 0; j < 7 - n; j++)
                    {
                        squareIndex += directionOffset;
                        int piece = board.Square[squareIndex];
                        rayMask |= 1ul << squareIndex;
                        if (piece != Piece.None)
                        {
                            if (Piece.IsColour(piece, friendlyColour))
                            {
                                // First friendly piece we have come across in this direction, so it might be pinned
                                if (!isFriendlyPieceAlongRay)
                                {
                                    isFriendlyPieceAlongRay = true;
                                }
                                // This is the second friendly piece we've found in this direction, therefore pin is not possible
                                else
                                {
                                    break;
                                }
                            }
                            // This square contains an enemy piece
                            else
                            {
                                int pieceType = Piece.PieceType(piece);

                                // Only rooks can Wrap!
                                if (pieceType == Piece.Rook)
                                {
                                    // Friendly piece blocks the check, so this is a pin
                                    if (isFriendlyPieceAlongRay)
                                    {
                                        pinsExistInPosition = true;
                                        pinRayBitmask.Add(rayMask);
                                    }
                                    // No friendly piece blocking the attack, so this is a check
                                    else
                                    {
                                        checkRayBitmask |= rayMask;
                                        conventionalDoubleCheck = inCheck;
                                        inCheck = true;
                                    }
                                }
                                else
                                {
                                    // This enemy piece is not able to move in the current direction, and so is blocking any checks/pins
                                }
                                break;
                            }
                        }
                    }
                }
                for (int i = 0; i < n; i++)
                {
                    int squareIndex = friendlyKingSquare + directionOffset * (i + 1);
                    rayMask |= 1ul << squareIndex;
                    int piece = board.Square[squareIndex];
                    if (directionOffset == -1)
                    {
                        piece++;
                        piece--;
                    }
                    // This square contains a piece
                    if (piece != Piece.None)
                    {
                        if (Piece.IsColour(piece, friendlyColour))
                        {
                            // First friendly piece we have come across in this direction, so it might be pinned
                            if (!isFriendlyPieceAlongRay)
                            {
                                isFriendlyPieceAlongRay = true;
                            }
                            // This is the second friendly piece we've found in this direction, therefore pin is not possible
                            else
                            {
                                break;
                            }
                        }
                        // This square contains an enemy piece
                        else
                        {
                            int pieceType = Piece.PieceType(piece);

                            // Check if piece is in bitmask of pieces able to move in current direction
                            if (isDiagonal && pieceType == Piece.Queen || !isDiagonal && Piece.IsRookOrQueen(pieceType))
                            {
                                // Friendly piece blocks the check, so this is a pin
                                if (isFriendlyPieceAlongRay)
                                {
                                    pinsExistInPosition = true;
                                    pinRayBitmask.Add(rayMask);
                                }
                                // No friendly piece blocking the attack, so this is a check
                                else
                                {
                                    checkRayBitmask |= rayMask;
                                    conventionalDoubleCheck = inCheck;
                                    inCheck = true;
                                }
                            }
                            else
                            {
                                // This enemy piece is not able to move in the current direction, and so is blocking any checks/pins
                            }
                            break;
                        }
                    }
                    //Check for rooks wrapping
                    if (isHorizontal && i == n - 1)
                    {
                        //Wrap
                        squareIndex += directionOffsets[dir - 2];
                        for (int j = 0; j < 7 - n; j++)
                        {
                            squareIndex += directionOffset;
                            rayMask |= 1ul << squareIndex;
                            piece = board.Square[squareIndex];
                            if (piece != Piece.None)
                            {
                                if (Piece.IsColour(piece, friendlyColour))
                                {
                                    // First friendly piece we have come across in this direction, so it might be pinned
                                    if (!isFriendlyPieceAlongRay)
                                    {
                                        isFriendlyPieceAlongRay = true;
                                    }
                                    // This is the second friendly piece we've found in this direction, therefore pin is not possible
                                    else
                                    {
                                        break;
                                    }
                                }
                                // This square contains an enemy piece
                                else
                                {
                                    int pieceType = Piece.PieceType(piece);

                                    // Only rooks can Wrap!
                                    if (pieceType == Piece.Rook)
                                    {
                                        // Friendly piece blocks the check, so this is a pin
                                        if (isFriendlyPieceAlongRay)
                                        {
                                            pinsExistInPosition = true;
                                            pinRayBitmask.Add(rayMask);
                                        }
                                        // No friendly piece blocking the attack, so this is a check
                                        else
                                        {
                                            checkRayBitmask |= rayMask;
                                            conventionalDoubleCheck = inCheck;
                                            inCheck = true;
                                        }
                                    }
                                    else
                                    {
                                        // This enemy piece is not able to move in the current direction, and so is blocking any checks/pins
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }

            }

            // Knight attacks
            PieceList opponentKnights = board.knights[opponentColourIndex];
            opponentKnightAttacks = 0;
            bool isKnightCheck = false;

            for (int knightIndex = 0; knightIndex < opponentKnights.Count; knightIndex++)
            {
                int startSquare = opponentKnights[knightIndex];
                opponentKnightAttacks |= knightAttackBitboards[startSquare];

                if (!isKnightCheck && BitBoardUtility.ContainsSquare(opponentKnightAttacks, friendlyKingSquare))
                {
                    isKnightCheck = true;
                    conventionalDoubleCheck = inCheck;
                    inCheck = true;
                    checkRayBitmask |= 1ul << startSquare;
                }
            }

            // Pawn attacks
            PieceList opponentPawns = board.pawns[opponentColourIndex];
            opponentPawnAttackMap = 0;
            bool isPawnCheck = false;

            for (int pawnIndex = 0; pawnIndex < opponentPawns.Count; pawnIndex++)
            {
                int pawnSquare = opponentPawns[pawnIndex];
                ulong pawnAttacks = pawnAttackBitboards[pawnSquare][opponentColourIndex];
                opponentPawnAttackMap |= pawnAttacks;

                if (!isPawnCheck && BitBoardUtility.ContainsSquare(pawnAttacks, friendlyKingSquare))
                {
                    isPawnCheck = true;
                    conventionalDoubleCheck = inCheck;
                    inCheck = true;
                    checkRayBitmask |= 1ul << pawnSquare;
                }
            }


            totalCheckMask |= checkRayBitmask;
            for (int i = 0; i < pinRayBitmask.Count; i++)
            {
                totalPinMask |= pinRayBitmask[i];
            }

            int enemyKingSquare = board.KingSquare[opponentColourIndex];

            opponentAttackMapNoPawns = opponentSlidingAttackMap | opponentKnightAttacks | kingAttackBitboards[enemyKingSquare] | opponentBishopAttackMap;
            opponentAttackMap = opponentAttackMapNoPawns | opponentPawnAttackMap;

            inCheck = inCheck || bishopCheckRays.Length > 0;
        }

        bool SquareIsAttacked(int square)
        {
            return BitBoardUtility.ContainsSquare(opponentAttackMap, square);
        }

        bool InCheckAfterEnPassant(int startSquare, int targetSquare, int epCapturedPawnSquare)
        {
            // Update board to reflect en-passant capture
            board.Square[targetSquare] = board.Square[startSquare];
            board.Square[startSquare] = Piece.None;
            board.Square[epCapturedPawnSquare] = Piece.None;
            

            MoveGenerator mg = new MoveGenerator();
            mg.board = board;
            mg.Init();
            mg.CalculateAttackData();
            bool inCheckAfterEP = mg.inCheck;
            // Undo change to board
            board.Square[targetSquare] = Piece.None;
            board.Square[startSquare] = Piece.Pawn | friendlyColour;
            board.Square[epCapturedPawnSquare] = Piece.Pawn | opponentColour;
            return inCheckAfterEP;
        }

    }

}