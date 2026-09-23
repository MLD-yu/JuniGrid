/* This file was derived from libmspack
 * (C) 2003-2004 Stuart Caie.
 * (C) 2011 Ali Scissons.
 *
 * The LZX method was created by Jonathan Forbes and Tomi Poutanen, adapted
 * by Microsoft Corporation.
 *
 * This source file is Dual licensed; meaning the end-user of this source file
 * may redistribute/modify it under the LGPL 2.1 or MS-PL licenses.
 */
/* GNU LESSER GENERAL PUBLIC LICENSE version 2.1
 * LzxDecoder is free software; you can redistribute it and/or modify it under
 * the terms of the GNU Lesser General Public License (LGPL) version 2.1
 */
/* MICROSOFT PUBLIC LICENSE
 * This source code is subject to the terms of the Microsoft Public License (Ms-PL).
 *
 * Redistribution and use in source and binary forms, with or without modification,
 * is permitted provided that redistributions of the source code retain the above
 * copyright notices and this file header.
 *
 * For details, see <http://www.opensource.org/licenses/ms-pl.html>.
 */
/* JuniGrid 移植说明（立绘页重建，2026-09）：
 *  - 与 MonoGame 版一致（XNB 位流是 16 位小端字、位序 MSB 先），窗口位数为 16。
 *  - MonoGame 版 intel E8 桩恒 return -1（其实解压早已完成）→ 按 v1 实测结论直接 return 0。
 *  - 静默 -1 错误码全部改成带标记的异常，排查时可读。 */

using System.IO;

namespace JuniGrid.Services;

/// <summary>LZX 解码遇到损坏/截断数据时抛出（带位置标记，替代原实现的静默 -1）。</summary>
public sealed class LzxDataException(string message) : Exception(message);

internal sealed class LzxDecoder
{
    private static uint[]? _positionBase;
    private static byte[]? _extraBits;

    private LzxState _st;

    public LzxDecoder(int window)
    {
        var wndsize = (uint)(1 << window);
        int posnSlots;

        if (window < 15 || window > 21) throw new LzxDataException("LZX 窗口位数必须在 15–21");

        _st = new LzxState();
        _st.Window = new byte[wndsize];
        for (var i = 0; i < wndsize; i++) _st.Window[i] = 0xDC;
        _st.WindowSize = wndsize;
        _st.WindowPosn = 0;

        if (_extraBits is null)
        {
            var eb = new byte[52];
            for (int i = 0, j = 0; i <= 50; i += 2)
            {
                eb[i] = eb[i + 1] = (byte)j;
                if (i != 0 && j < 17) j++;
            }
            _extraBits = eb;
        }
        if (_positionBase is null)
        {
            var pb = new uint[51];
            for (int i = 0, j = 0; i <= 50; i++)
            {
                pb[i] = (uint)j;
                j += 1 << _extraBits[i];
            }
            _positionBase = pb;
        }

        if (window == 20) posnSlots = 42;
        else if (window == 21) posnSlots = 50;
        else posnSlots = window << 1;

        _st.R0 = _st.R1 = _st.R2 = 1;
        _st.MainElements = (ushort)(LzxConstants.NumChars + (posnSlots << 3));
        _st.HeaderRead = 0;
        _st.BlockRemaining = 0;
        _st.BlockType = BlockType.Invalid;

        _st.PretreeTable = new ushort[(1 << LzxConstants.PretreeTableBits) + (LzxConstants.PretreeMaxSymbols << 1)];
        _st.PretreeLen = new byte[LzxConstants.PretreeMaxSymbols + LzxConstants.LenTableSafety];
        _st.MaintreeTable = new ushort[(1 << LzxConstants.MaintreeTableBits) + (LzxConstants.MaintreeMaxSymbols << 1)];
        _st.MaintreeLen = new byte[LzxConstants.MaintreeMaxSymbols + LzxConstants.LenTableSafety];
        _st.LengthTable = new ushort[(1 << LzxConstants.LengthTableBits) + (LzxConstants.LengthMaxSymbols << 1)];
        _st.LengthLen = new byte[LzxConstants.LengthMaxSymbols + LzxConstants.LenTableSafety];
        _st.AlignedTable = new ushort[(1 << LzxConstants.AlignedTableBits) + (LzxConstants.AlignedMaxSymbols << 1)];
        _st.AlignedLen = new byte[LzxConstants.AlignedMaxSymbols + LzxConstants.LenTableSafety];
        // 增量解码时长度表要在上一帧的基础上做 delta，必须清零
        Array.Clear(_st.MaintreeLen);
        Array.Clear(_st.LengthLen);
    }

    /// <summary>解压一段 LZX 帧：inData 从当前位置读 inLen 字节，向 outData 写 outLen 字节。
    /// 同一数据流的多帧必须复用同一个 LzxDecoder 实例（树/窗口/R0R1R2 跨帧延续）。</summary>
    public void Decompress(Stream inData, int inLen, Stream outData, int outLen)
    {
        var bitbuf = new BitBuffer(inData);
        var startpos = inData.Position;
        var endpos = inData.Position + inLen;

        var window = _st.Window;
        var windowPosn = _st.WindowPosn;
        var windowSize = _st.WindowSize;
        uint r0 = _st.R0, r1 = _st.R1, r2 = _st.R2;
        uint i, j;

        var togo = outLen;
        int thisRun, mainElement, matchLength, matchOffset, lengthFooter, extra, verbatimBits;
        int rundest, runsrc, copyLength, alignedBits;

        bitbuf.InitBitStream();

        if (_st.HeaderRead == 0)
        {
            var intel = bitbuf.ReadBits(1);
            if (intel != 0)
            {
                // intel E8 过滤已停用，但这两个 16 位字段必须照样读走，
                 // 否则位流错位会让后续块头解析全部跑偏
                 _ = bitbuf.ReadBits(16); _ = bitbuf.ReadBits(16);
            }
            _st.HeaderRead = 1;
        }

        while (togo > 0)
        {
            if (_st.BlockRemaining == 0)
            {
                if (_st.BlockType == BlockType.Uncompressed)
                {
                    if ((_st.BlockLength & 1) == 1) inData.ReadByte();  // 对齐到字
                    bitbuf.InitBitStream();
                }

                _st.BlockType = (BlockType)bitbuf.ReadBits(3);
                i = bitbuf.ReadBits(16);
                j = bitbuf.ReadBits(8);
                _st.BlockRemaining = _st.BlockLength = (uint)((i << 8) | j);

                switch (_st.BlockType)
                {
                    case BlockType.Aligned:
                        for (i = 0, j = 0; i < 8; i++)
                        {
                            j = bitbuf.ReadBits(3);
                            _st.AlignedLen[i] = (byte)j;
                        }
                        MakeDecodeTable(LzxConstants.AlignedMaxSymbols, LzxConstants.AlignedTableBits,
                            _st.AlignedLen, _st.AlignedTable);
                        // aligned 头的剩余部分与 verbatim 相同
                        goto case BlockType.Verbatim;

                    case BlockType.Verbatim:
                        ReadLengths(_st.MaintreeLen, 0, 256, bitbuf);
                        ReadLengths(_st.MaintreeLen, 256, _st.MainElements, bitbuf);
                        MakeDecodeTable(LzxConstants.MaintreeMaxSymbols, LzxConstants.MaintreeTableBits,
                            _st.MaintreeLen, _st.MaintreeTable);

                        ReadLengths(_st.LengthLen, 0, LzxConstants.NumSecondaryLengths, bitbuf);
                        MakeDecodeTable(LzxConstants.LengthMaxSymbols, LzxConstants.LengthTableBits,
                            _st.LengthLen, _st.LengthTable);
                        break;

                    case BlockType.Uncompressed:
                        bitbuf.EnsureBits(16);
                        if (bitbuf.GetBitsLeft() > 16) inData.Seek(-2, SeekOrigin.Current);
                        int lo, ml, mh, hi;
                        lo = inData.ReadByte(); ml = inData.ReadByte(); mh = inData.ReadByte(); hi = inData.ReadByte();
                        if (lo < 0 || ml < 0 || mh < 0 || hi < 0)
                            throw new LzxDataException("LZX 未压缩块 R0 截断");
                        r0 = (uint)(lo | ml << 8 | mh << 16 | hi << 24);
                        lo = inData.ReadByte(); ml = inData.ReadByte(); mh = inData.ReadByte(); hi = inData.ReadByte();
                        if (lo < 0 || ml < 0 || mh < 0 || hi < 0)
                            throw new LzxDataException("LZX 未压缩块 R1 截断");
                        r1 = (uint)(lo | ml << 8 | mh << 16 | hi << 24);
                        lo = inData.ReadByte(); ml = inData.ReadByte(); mh = inData.ReadByte(); hi = inData.ReadByte();
                        if (lo < 0 || ml < 0 || mh < 0 || hi < 0)
                            throw new LzxDataException("LZX 未压缩块 R2 截断");
                        r2 = (uint)(lo | ml << 8 | mh << 16 | hi << 24);
                        break;

                    default:
                        throw new LzxDataException($"LZX 非法块类型 {_st.BlockType}");
                }
            }

            if (inData.Position > startpos + inLen)
            {
                // 最后一 run 可能不足 16 位：建表时预读的位不属于有效数据，
                // 但剩余不足 16 位（超出 2 字节护栏）就是真截断
                if (inData.Position > startpos + inLen + 2 || bitbuf.GetBitsLeft() < 16)
                    throw new LzxDataException($"LZX 输入耗尽 pos={inData.Position - startpos}/{inLen}");
            }

            while ((thisRun = (int)_st.BlockRemaining) > 0 && togo > 0)
            {
                if (thisRun > togo) thisRun = togo;
                togo -= thisRun;
                _st.BlockRemaining -= (uint)thisRun;

                windowPosn &= windowSize - 1;
                if (windowPosn + thisRun > windowSize)
                    throw new LzxDataException("LZX run 跨越窗口回绕");

                switch (_st.BlockType)
                {
                    case BlockType.Verbatim:
                        while (thisRun > 0)
                        {
                            mainElement = (int)ReadHuffSym(_st.MaintreeTable, _st.MaintreeLen,
                                LzxConstants.MaintreeMaxSymbols, LzxConstants.MaintreeTableBits, bitbuf);
                            if (mainElement < LzxConstants.NumChars)
                            {
                                window[windowPosn++] = (byte)mainElement;
                                thisRun--;
                            }
                            else
                            {
                                mainElement -= LzxConstants.NumChars;

                                matchLength = mainElement & LzxConstants.NumPrimaryLengths;
                                if (matchLength == LzxConstants.NumPrimaryLengths)
                                {
                                    lengthFooter = (int)ReadHuffSym(_st.LengthTable, _st.LengthLen,
                                        LzxConstants.LengthMaxSymbols, LzxConstants.LengthTableBits, bitbuf);
                                    matchLength += lengthFooter;
                                }
                                matchLength += LzxConstants.MinMatch;

                                matchOffset = mainElement >> 3;

                                if (matchOffset > 2)
                                {
                                    if (matchOffset != 3)
                                    {
                                        extra = _extraBits![matchOffset];
                                        verbatimBits = (int)bitbuf.ReadBits((byte)extra);
                                        matchOffset = (int)_positionBase![matchOffset] - 2 + verbatimBits;
                                    }
                                    else
                                    {
                                        matchOffset = 1;
                                    }
                                    r2 = r1; r1 = r0; r0 = (uint)matchOffset;
                                }
                                else if (matchOffset == 0)
                                {
                                    matchOffset = (int)r0;
                                }
                                else if (matchOffset == 1)
                                {
                                    matchOffset = (int)r1;
                                    r1 = r0; r0 = (uint)matchOffset;
                                }
                                else // matchOffset == 2
                                {
                                    matchOffset = (int)r2;
                                    r2 = r0; r0 = (uint)matchOffset;
                                }

                                rundest = (int)windowPosn;
                                thisRun -= matchLength;

                                if (windowPosn >= matchOffset)
                                {
                                    runsrc = rundest - matchOffset;
                                }
                                else
                                {
                                    runsrc = rundest + ((int)windowSize - matchOffset);
                                    copyLength = matchOffset - (int)windowPosn;
                                    if (copyLength < matchLength)
                                    {
                                        matchLength -= copyLength;
                                        windowPosn += (uint)copyLength;
                                        while (copyLength-- > 0) window[rundest++] = window[runsrc++];
                                        runsrc = 0;
                                    }
                                }
                                windowPosn += (uint)matchLength;

                                while (matchLength-- > 0) window[rundest++] = window[runsrc++];
                            }
                        }
                        break;

                    case BlockType.Aligned:
                        while (thisRun > 0)
                        {
                            mainElement = (int)ReadHuffSym(_st.MaintreeTable, _st.MaintreeLen,
                                LzxConstants.MaintreeMaxSymbols, LzxConstants.MaintreeTableBits, bitbuf);

                            if (mainElement < LzxConstants.NumChars)
                            {
                                window[windowPosn++] = (byte)mainElement;
                                thisRun--;
                            }
                            else
                            {
                                mainElement -= LzxConstants.NumChars;

                                matchLength = mainElement & LzxConstants.NumPrimaryLengths;
                                if (matchLength == LzxConstants.NumPrimaryLengths)
                                {
                                    lengthFooter = (int)ReadHuffSym(_st.LengthTable, _st.LengthLen,
                                        LzxConstants.LengthMaxSymbols, LzxConstants.LengthTableBits, bitbuf);
                                    matchLength += lengthFooter;
                                }
                                matchLength += LzxConstants.MinMatch;

                                matchOffset = mainElement >> 3;

                                if (matchOffset > 2)
                                {
                                    extra = _extraBits![matchOffset];
                                    matchOffset = (int)_positionBase![matchOffset] - 2;
                                    if (extra > 3)
                                    {
                                        extra -= 3;
                                        verbatimBits = (int)bitbuf.ReadBits((byte)extra);
                                        matchOffset += verbatimBits << 3;
                                        alignedBits = (int)ReadHuffSym(_st.AlignedTable, _st.AlignedLen,
                                            LzxConstants.AlignedMaxSymbols, LzxConstants.AlignedTableBits, bitbuf);
                                        matchOffset += alignedBits;
                                    }
                                    else if (extra == 3)
                                    {
                                        alignedBits = (int)ReadHuffSym(_st.AlignedTable, _st.AlignedLen,
                                            LzxConstants.AlignedMaxSymbols, LzxConstants.AlignedTableBits, bitbuf);
                                        matchOffset += alignedBits;
                                    }
                                    else if (extra > 0)
                                    {
                                        verbatimBits = (int)bitbuf.ReadBits((byte)extra);
                                        matchOffset += verbatimBits;
                                    }
                                    else
                                    {
                                        matchOffset = 1;
                                    }
                                    r2 = r1; r1 = r0; r0 = (uint)matchOffset;
                                }
                                else if (matchOffset == 0)
                                {
                                    matchOffset = (int)r0;
                                }
                                else if (matchOffset == 1)
                                {
                                    matchOffset = (int)r1;
                                    r1 = r0; r0 = (uint)matchOffset;
                                }
                                else // matchOffset == 2
                                {
                                    matchOffset = (int)r2;
                                    r2 = r0; r0 = (uint)matchOffset;
                                }

                                rundest = (int)windowPosn;
                                thisRun -= matchLength;

                                if (windowPosn >= matchOffset)
                                {
                                    runsrc = rundest - matchOffset;
                                }
                                else
                                {
                                    runsrc = rundest + ((int)windowSize - matchOffset);
                                    copyLength = matchOffset - (int)windowPosn;
                                    if (copyLength < matchLength)
                                    {
                                        matchLength -= copyLength;
                                        windowPosn += (uint)copyLength;
                                        while (copyLength-- > 0) window[rundest++] = window[runsrc++];
                                        runsrc = 0;
                                    }
                                }
                                windowPosn += (uint)matchLength;

                                while (matchLength-- > 0) window[rundest++] = window[runsrc++];
                            }
                        }
                        break;

                    case BlockType.Uncompressed:
                        if (inData.Position + thisRun > endpos)
                            throw new LzxDataException("LZX 未压缩块越界");
                        var temp = new byte[thisRun];
                        var got = inData.Read(temp, 0, thisRun);
                        if (got != thisRun) throw new LzxDataException("LZX 未压缩块截断");
                        temp.CopyTo(window, (int)windowPosn);
                        windowPosn += (uint)thisRun;
                        break;

                    default:
                        throw new LzxDataException($"LZX 非法块类型 {_st.BlockType}");
                }
            }
        }

        if (togo != 0)
            throw new LzxDataException("LZX 输出不足");

        var startWindowPos = (int)windowPosn;
        if (startWindowPos == 0) startWindowPos = (int)windowSize;
        startWindowPos -= outLen;
        outData.Write(window, startWindowPos, outLen);

        _st.WindowPosn = windowPosn;
        _st.R0 = r0;
        _st.R1 = r1;
        _st.R2 = r2;

        // intel E8 转换桩：MonoGame 版在此恒 return -1（解压其实已完成）。
        // XNB 内容不受 intel 转换影响，直接按成功返回（v1 实测结论）。
    }

    private void MakeDecodeTable(uint nsyms, uint nbits, byte[] length, ushort[] table)
    {
        ushort sym;
        uint leaf;
        var bitNum = 1;
        uint fill;
        uint pos = 0;
        uint tableMask = (uint)(1 << (int)nbits);
        uint bitMask = tableMask >> 1;
        uint nextSymbol = bitMask;

        while (bitNum <= nbits)
        {
            for (sym = 0; sym < nsyms; sym++)
            {
                if (length[sym] == bitNum)
                {
                    leaf = pos;
                    if ((pos += bitMask) > tableMask)
                        throw new LzxDataException("LZX 解码表溢出(短码)");

                    fill = bitMask;
                    while (fill-- > 0) table[leaf++] = sym;
                }
            }
            bitMask >>= 1;
            bitNum++;
        }

        if (pos != tableMask)
        {
            for (sym = (ushort)pos; sym < tableMask; sym++) table[sym] = 0;

            pos <<= 16;
            tableMask <<= 16;
            bitMask = 1u << 15;

            while (bitNum <= 16)
            {
                for (sym = 0; sym < nsyms; sym++)
                {
                    if (length[sym] == bitNum)
                    {
                        leaf = pos >> 16;
                        for (fill = 0; fill < bitNum - nbits; fill++)
                        {
                            if (table[leaf] == 0)
                            {
                                table[nextSymbol << 1] = 0;
                                table[(nextSymbol << 1) + 1] = 0;
                                table[leaf] = (ushort)(nextSymbol++);
                            }
                            leaf = (uint)(table[leaf] << 1);
                            if (((pos >> (int)(15 - fill)) & 1) == 1) leaf++;
                        }
                        table[leaf] = sym;

                        if ((pos += bitMask) > tableMask)
                            throw new LzxDataException("LZX 解码表溢出(长码)");
                    }
                }
                bitMask >>= 1;
                bitNum++;
            }
        }

        if (pos == tableMask) return;

        for (sym = 0; sym < nsyms; sym++)
            if (length[sym] != 0)
                throw new LzxDataException("LZX 非法解码表");
    }

    private void ReadLengths(byte[] lens, uint first, uint last, BitBuffer bitbuf)
    {
        uint x, y;
        int z;

        for (x = 0; x < 20; x++)
        {
            y = bitbuf.ReadBits(4);
            _st.PretreeLen[x] = (byte)y;
        }
        MakeDecodeTable(LzxConstants.PretreeMaxSymbols, LzxConstants.PretreeTableBits,
            _st.PretreeLen, _st.PretreeTable);

        for (x = first; x < last;)
        {
            z = (int)ReadHuffSym(_st.PretreeTable, _st.PretreeLen,
                LzxConstants.PretreeMaxSymbols, LzxConstants.PretreeTableBits, bitbuf);
            if (z == 17)
            {
                y = bitbuf.ReadBits(4); y += 4;
                while (y-- != 0) lens[x++] = 0;
            }
            else if (z == 18)
            {
                y = bitbuf.ReadBits(5); y += 20;
                while (y-- != 0) lens[x++] = 0;
            }
            else if (z == 19)
            {
                y = bitbuf.ReadBits(1); y += 4;
                z = (int)ReadHuffSym(_st.PretreeTable, _st.PretreeLen,
                    LzxConstants.PretreeMaxSymbols, LzxConstants.PretreeTableBits, bitbuf);
                z = lens[x] - z; if (z < 0) z += 17;
                while (y-- != 0) lens[x++] = (byte)z;
            }
            else
            {
                z = lens[x] - z; if (z < 0) z += 17;
                lens[x++] = (byte)z;
            }
        }
    }

    private uint ReadHuffSym(ushort[] table, byte[] lengths, uint nsyms, uint nbits, BitBuffer bitbuf)
    {
        uint i, j;
        bitbuf.EnsureBits(16);
        if ((i = table[bitbuf.PeekBits((byte)nbits)]) >= nsyms)
        {
            j = 1u << (32 - (int)nbits);
            do
            {
                j >>= 1; i <<= 1; i |= (bitbuf.GetBuffer() & j) != 0 ? 1u : 0u;
                if (j == 0) throw new LzxDataException("LZX Huffman 解码越界");
            } while ((i = table[i]) >= nsyms);
        }
        j = lengths[i];
        bitbuf.RemoveBits((byte)j);

        return i;
    }

    private class BitBuffer
    {
        private uint _buffer;
        private byte _bitsLeft;
        private readonly Stream _byteStream;

        public BitBuffer(Stream stream)
        {
            _byteStream = stream;
            InitBitStream();
        }

        public void InitBitStream()
        {
            _buffer = 0;
            _bitsLeft = 0;
        }

        public void EnsureBits(byte bits)
        {
            while (_bitsLeft < bits)
            {
                // 流尾允许预读越过有效数据（读取侧有 16 位护栏校验），
                // 与 MonoGame 原版一致：(byte)(-1) = 0xFF 填充
                var lo = (byte)_byteStream.ReadByte();
                var hi = (byte)_byteStream.ReadByte();
                _buffer |= (uint)(((hi << 8) | lo) << (32 - 16 - _bitsLeft));
                _bitsLeft += 16;
            }
        }

        public uint PeekBits(byte bits) => _buffer >> (32 - bits);

        public void RemoveBits(byte bits)
        {
            _buffer <<= bits;
            _bitsLeft -= bits;
        }

        public uint ReadBits(byte bits)
        {
            uint ret = 0;
            if (bits > 0)
            {
                EnsureBits(bits);
                ret = PeekBits(bits);
                RemoveBits(bits);
            }
            return ret;
        }

        public uint GetBuffer() => _buffer;

        public byte GetBitsLeft() => _bitsLeft;
    }

    private struct LzxState
    {
        public uint R0, R1, R2;
        public ushort MainElements;
        public int HeaderRead;
        public BlockType BlockType;
        public uint BlockLength;
        public uint BlockRemaining;

        public ushort[] PretreeTable;
        public byte[] PretreeLen;
        public ushort[] MaintreeTable;
        public byte[] MaintreeLen;
        public ushort[] LengthTable;
        public byte[] LengthLen;
        public ushort[] AlignedTable;
        public byte[] AlignedLen;

        public byte[] Window;
        public uint WindowSize;
        public uint WindowPosn;
    }

    private enum BlockType
    {
        Invalid = 0,
        Verbatim = 1,
        Aligned = 2,
        Uncompressed = 3
    }

    private static class LzxConstants
    {
        public const ushort MinMatch = 2;
        public const ushort NumChars = 256;
        public const ushort NumPrimaryLengths = 7;
        public const ushort NumSecondaryLengths = 249;

        public const ushort PretreeMaxSymbols = 20;
        public const ushort PretreeTableBits = 6;
        public const ushort MaintreeMaxSymbols = NumChars + 50 * 8;
        public const ushort MaintreeTableBits = 12;
        public const ushort LengthMaxSymbols = NumSecondaryLengths + 1;
        public const ushort LengthTableBits = 12;
        public const ushort AlignedMaxSymbols = 8;
        public const ushort AlignedTableBits = 7;
        public const ushort LenTableSafety = 64;
    }
}
