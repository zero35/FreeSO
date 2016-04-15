﻿/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FSO.SimAntics.Engine;
using FSO.Files.Utils;
using FSO.SimAntics.Engine.Scopes;
using FSO.SimAntics.Engine.Utils;
using System.IO;
using FSO.SimAntics.Model.TSOPlatform;
using FSO.SimAntics.NetPlay.Model.Commands;

namespace FSO.SimAntics.Primitives
{
    public class VMTransferFunds : VMPrimitiveHandler
    {
        public override VMPrimitiveExitCode Execute(VMStackFrame context, VMPrimitiveOperand args)
        {
            var operand = (VMTransferFundsOperand)args;

            //first of all... are we in an async wait state?
            if (context.Thread.BlockingState != null)
            {
                var state = (VMTransferFundsState)context.Thread.BlockingState; //crash here if not intended state.
                if (state.Responded)
                {
                    context.Thread.BlockingState = null;
                    if (operand.TransferType == VMTransferFundsType.PutStackObjectCashIntoTempXL0)
                    {
                        context.Thread.TempXL[0] = (int)state.Budget1;
                        return VMPrimitiveExitCode.GOTO_TRUE;
                    }
                    else return (state.Success) ? VMPrimitiveExitCode.GOTO_TRUE : VMPrimitiveExitCode.GOTO_FALSE;
                }
                else return VMPrimitiveExitCode.CONTINUE_NEXT_TICK;
            }

            var amount = VMMemory.GetBigVariable(context, operand.GetAmountOwner(), (short)operand.AmountData);

            uint source = uint.MaxValue;
            uint target = uint.MaxValue;

            switch (operand.TransferType)
            {
                case VMTransferFundsType.MeToMaxis:
                    source = context.Caller.PersistID; break;
                case VMTransferFundsType.MaxisToMe:
                    target = context.Caller.PersistID; break;
                case VMTransferFundsType.MaxisToStackObjectsOwner:
                    //TODO: when TSOGameObjectState is in
                    break;
                case VMTransferFundsType.StackObjectsOwnerToMaxis:
                    break;
                case VMTransferFundsType.FromMeToStackObject:
                    source = context.Caller.PersistID; target = context.StackObject.PersistID; break;
                case VMTransferFundsType.FromStackObjectToMe:
                    source = context.StackObject.PersistID; target = context.Caller.PersistID; break;
                case VMTransferFundsType.FromMaxisToStackObject:
                    target = context.StackObject.PersistID; break;
                case VMTransferFundsType.FromStackObjectToMaxis:
                    source = context.StackObject.PersistID; break;
                case VMTransferFundsType.FromStackObjectToStackObjectOwner:
                    break;
                case VMTransferFundsType.PutStackObjectCashIntoTempXL0:
                    source = context.StackObject.PersistID; amount = 0;
                    break;
                case VMTransferFundsType.FromLotOwnerToMaxis:
                    //TODO: when TSOLotState is in
                    break;
                case VMTransferFundsType.FromMaxisToLotOwner:
                    break;
                default:
                    return VMPrimitiveExitCode.GOTO_TRUE;

            }
            //TODO: LotRoommates transactions, shared account (but that's unused...)

            //if we're in a check tree, transactions must be instant and test only.
            if (context.Thread.IsCheck)
            {
                if (operand.TransferType == VMTransferFundsType.PutStackObjectCashIntoTempXL0)
                {
                    context.Thread.TempXL[0] = (int)context.StackObject.TSOState.Budget.Value;
                    return VMPrimitiveExitCode.GOTO_TRUE;
                }
                if (!operand.JustTest) return VMPrimitiveExitCode.GOTO_FALSE;
                if (context.VM.CheckGlobalLink.PerformTransaction(context.VM, true, source, target, amount))
                    return VMPrimitiveExitCode.GOTO_TRUE;
                return VMPrimitiveExitCode.GOTO_FALSE;
            }
            else
            {
                //otherwise, we can wait... make an async call to the transaction handler to process our request.
                //the response will be dealt with on a later tick.

                context.Thread.BlockingState = new VMTransferFundsState();

                if (context.VM.GlobalLink != null)
                {
                    //get id and vm now to avoid race conditions
                    var id = context.Caller.ObjectID; //this thread's object id
                    var vm = context.VM;
                    context.VM.GlobalLink.PerformTransaction(context.VM, operand.JustTest, source, target, amount,
                        (bool success, uint uid1, uint budget1, uint uid2, uint budget2) =>
                        {
                            vm.SendCommand(new VMNetAsyncResponseCmd(id, new VMTransferFundsState
                            {
                                Responded = true,
                                Success = success,
                                UID1 = uid1,
                                Budget1 = budget1,
                                UID2 = uid2,
                                Budget2 = budget2
                            }));
                        });
                }
                return VMPrimitiveExitCode.CONTINUE_NEXT_TICK;
            }
        }
    }

    public class VMTransferFundsOperand : VMPrimitiveOperand
    {
        public VMTransferFundsOldOwner OldAmountOwner;
        public VMVariableScope AmountOwner;
        public ushort AmountData;
        public VMTransferFundsFlags Flags;
        public VMTransferFundsExpenseType ExpenseType;
        public VMTransferFundsType TransferType;

        public bool JustTest
        {
            get
            {
                return (Flags & VMTransferFundsFlags.JustTest) > 0;
            }
            set
            {
                if (value) Flags |= VMTransferFundsFlags.JustTest;
                else Flags &= ~VMTransferFundsFlags.JustTest;
            }
        }

        #region VMPrimitiveOperand Members
        public void Read(byte[] bytes)
        {
            using (var io = IoBuffer.FromBytes(bytes, ByteOrder.LITTLE_ENDIAN)){
                OldAmountOwner = (VMTransferFundsOldOwner)io.ReadByte();
                AmountOwner = (VMVariableScope)io.ReadByte();
                AmountData = io.ReadUInt16();
                Flags = (VMTransferFundsFlags)io.ReadByte();
                io.ReadByte();
                ExpenseType = (VMTransferFundsExpenseType)io.ReadByte();
                TransferType = (VMTransferFundsType)io.ReadByte();
            }
        }
        public void Write(byte[] bytes) {
            using (var io = new BinaryWriter(new MemoryStream(bytes)))
            {
                io.Write((byte)OldAmountOwner);
                io.Write((byte)AmountOwner);
                io.Write((uint)Flags);
                io.Write((byte)ExpenseType);
                io.Write((byte)TransferType);
            }
        }
        #endregion

        public VMVariableScope GetAmountOwner()
        {
            switch (OldAmountOwner){
                case VMTransferFundsOldOwner.Literal:
                    return VMVariableScope.Literal;
                case VMTransferFundsOldOwner.Parameters:
                    return VMVariableScope.Parameters;
                case VMTransferFundsOldOwner.Local:
                    return VMVariableScope.Local;

                default:
                case VMTransferFundsOldOwner.Normal:
                    return AmountOwner;
            }
        }
    }

    public enum VMTransferFundsExpenseType
    { //these are probably used for logging or enforcing special behaviour
        NONE = 0,
        IncomeMisc = 1,
        IncomeFromPlayers = 2,
        IncomeFromObjects = 3,
        IncomeCheat = 4,
        ExpenseMisc = 5,
        ExpenseObjectRefillsAndMaintinance = 6,
        ExpenseFromObjects = 7,
        SimToSim = 8,

        //income from players and objects used for various purposes
        IncomePlayersSkill = 9,
        IncomeObjectsSkill = 10,
        IncomePlayersPizza = 11,
        IncomeObjectsPizza = 12,
        IncomePlayersPaperC = 13,
        IncomeObjectsPaperC = 14,
        IncomePlayersMaze = 15,
        IncomeObjectsMaze = 16,
        IncomePlayersRoulette = 17,
        IncomeObjectsRoulette = 18,
        IncomePlayersSlots = 19,
        IncomeObjectsSlots = 20,
        IncomePlayersBlackjack = 21,
        IncomeObjectsBlackjack = 22,
        IncomePlayersPoker = 23,
        IncomeObjectsPoker = 24,

        ExpenseNPC = 25,
        ExpenseNPCGardener = 26,
        ExpenseNPCMaid = 27,
        ExpenseNPCRepairman = 28,
        ExpenseNPCButler = 29, //???

        IncomeJob = 30,
        IncomeRobotJob = 31,
        IncomeRestaurantJob = 32,
        IncomeClubJob = 33,

        CSRRequestFunds = 34,
        CashInRequest = 35,
        CashIn = 36,
        CashOutRequest = 37,
        CashOut = 38,
        TransferToSharedAccount = 39,

        DoorCharge = 40,
        IncomeTypewriter = 41,
        IncomeEasel = 42,
        IncomeEaselPlayers = 43,
        IncomeChalkboard = 44,
        IncomeCanning = 45,
        IncomeChemistry = 46,
        IncomeGGWorkbench = 47,
        IncomePinata = 48,
        IncomePinataPlayers = 49,
        IncomeTelemarket = 50,
        IncomeTipJarPlayers = 51,
        ExpenseTipJar = 52,
        ExpenseBedHeart = 53,
        IncomeBedHeartPlayers = 54,
        ExpenseBuffet = 55,
        IncomeBuffetPlayers = 56,
        IncomeBuffetVacPlayers = 57,
        ExpenseViffetVac = 58,
        IncomeSlotPlayers = 59,
        ExpenseCheats = 60,
        IncomeFoodCounterPlayers = 61,
        UNKNOWN = 62,
        ExpenseFoodCounter = 63,
        ExpenseSnackMachine = 64,
        IncomeSnackMachinePlayers = 65,
        ExpenseShackMachineRefill = 66,
        ExpenseSodaMachine = 67,
        IncomeSodaMachinePlayers = 68,
        ExpenseSodaMachineRefill = 69,
        IncomePinballPlayers = 70,
        ExpensePinball = 71,
        ExpenseFridgeRefill = 72
    }
    

    public enum VMTransferFundsType {
        DEPRICATED_ADD = 0,
        DEPRICATED_SUBTRACT = 1,
        MeToMaxis = 2,
        DEPRICATED_ME_TO_STACK_OBJECT = 3,
        DEPRICATED_ME_TO_STACK_OBJECTS_OWNER = 4,
        MaxisToMe = 5,
        MaxisToStackObjectsOwner = 6,
        StackObjectsOwnerToMaxis = 7,
        DEPRICATED_OWNER_OF_TEMP_0_TO_OWNER_OF_TEMP_1 = 8,
        FromMeToStackObject = 9,
        FromStackObjectToMe = 10,
        FromMaxisToStackObject = 11,
        FromStackObjectToMaxis = 12,
        FromStackObjectToStackObjectOwner = 13,
        DEPRIACTED_FROM_OWNER_OF_OBJECT_TO_OBJECT = 14,
        DEPRICATED_FROM_TEMP_0_TO_OWNER_OF_TEMP_1 = 15,
        DEPRICATED_FROM_OWNER_OF_TEMP_0_TO_TEMP_1 = 16,
        PutStackObjectCashIntoTempXL0 = 17,
        FromStackObjectToLotRoommates = 18,
        FromLotOwnerToMaxis = 19,
        FromMaxisToLotOwner = 20,
        UNKNOWN = 21,
        CharacterToSharedAccount = 22,
        SharedAccountToCharacter = 23,
        MeToLotRoommates = 24
    }

    public enum VMTransferFundsOldOwner {
        Literal = 0,
        Parameters = 1,
        Local = 2,
        Normal = 3
    }

    [Flags]
    public enum VMTransferFundsFlags : byte
    {
        JustTest = 0x1,
        Subtract = 0x2
    }

    public class VMTransferFundsState : VMAsyncState
    {
        public bool Success;
        public uint UID1;
        public uint Budget1;
        public uint UID2;
        public uint Budget2;

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Success = reader.ReadBoolean();
            UID1 = reader.ReadUInt32();
            Budget1 = reader.ReadUInt32();
            UID2 = reader.ReadUInt32();
            Budget2 = reader.ReadUInt32();
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(Success);
            writer.Write(UID1);
            writer.Write(Budget1);
            writer.Write(UID2);
            writer.Write(Budget2);
        }
    }
}
