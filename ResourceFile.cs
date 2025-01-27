﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SystemShockPatcher;

public class ResourceFile : IDisposable {
  private readonly MemoryStream fileStream;
  private readonly BinaryReader binaryReader;

  private readonly Dictionary<ushort, ResourceInfo> resourceEntries;

  public IReadOnlyDictionary<ushort, ResourceInfo> ResourceEntries => resourceEntries;

  public ResourceFile(byte[] resFileData) {
    fileStream = new MemoryStream(resFileData, false);
    binaryReader = new BinaryReader(fileStream, Encoding.ASCII);

    var header = binaryReader.Read<FileHeader>();
    
    if (header.GetSignature() != FileHeader.FILE_SIGNATURE)
      throw new NotSupportedException($"File type is not supported ({header.GetSignature()})");

    #region Resource directory
    fileStream.Position = header.DirectoryOffset;
    var directoryHeader = binaryReader.Read<DirectoryHeader>();
    
    ushort resourceCount = directoryHeader.NumberOfEntries;
    long dataOffset = directoryHeader.DataOffset;
    
    resourceEntries = new Dictionary<ushort, ResourceInfo>(resourceCount);

    while (resourceCount-- > 0) {
      DirectoryEntry directoryEntry = binaryReader.Read<DirectoryEntry>();
      ResourceInfo resourceInfo = new() {
        info = directoryEntry,
        dataOffset = dataOffset
      };

      resourceEntries.Add(directoryEntry.Id, resourceInfo);

      dataOffset += (directoryEntry.LengthPacked + 3) & ~0x3; // 4-byte alignment
    }
    #endregion
  }

  public ResourceInfo GetResourceInfo(ushort resourceId) {
    return resourceEntries[resourceId];
  }

  public BinaryReader GetBinaryReader(long position) {
    fileStream.Position = position;
    return binaryReader;
  }

  public byte[] GetResourceData(ushort resourceId, ushort blockIndex = 0) => GetResourceData(resourceEntries[resourceId], blockIndex);
  public byte[] GetResourceData(ResourceInfo resourceInfo, ushort blockIndex = 0) {
    DirectoryEntry directoryEntry = resourceInfo.info;

    if (blockIndex != 0 && !directoryEntry.Flags.HasFlag(ResourceFlags.Compound))
      throw new Exception("Tried to access block but there are not any.");

    fileStream.Position = resourceInfo.dataOffset;
    byte[] rawData = binaryReader.ReadBytes(directoryEntry.LengthPacked);
    return ReadBlock(rawData, directoryEntry, blockIndex);
  }

  public byte[][] GetResourceData(ushort resourceId) => GetResourceData(resourceEntries[resourceId]);
  public byte[][] GetResourceData(ResourceInfo resourceInfo) {
    DirectoryEntry directoryEntry = resourceInfo.info;

    fileStream.Position = resourceInfo.dataOffset;
    byte[] rawData = binaryReader.ReadBytes(directoryEntry.LengthPacked);
    return ReadBlock(rawData, directoryEntry);
  }

  public T GetResourceData<T>(ushort resourceId, ushort blockIndex = 0) {
    return GetResourceData<T>(resourceEntries[resourceId], blockIndex);
  }

  public T GetResourceData<T>(ResourceInfo resourceInfo, ushort blockIndex = 0) {
    byte[] data = GetResourceData(resourceInfo, blockIndex);
    return data.Read<T>();
  }

  public T[] GetResourceDataArray<T>(ushort resourceId, ushort blockIndex = 0) => GetResourceDataArray<T>(resourceEntries[resourceId], blockIndex);
  public T[] GetResourceDataArray<T>(ResourceInfo resourceInfo, ushort blockIndex = 0) {
    DirectoryEntry directoryEntry = resourceInfo.info;

    if (blockIndex != 0 && !directoryEntry.Flags.HasFlag(ResourceFlags.Compound))
      throw new Exception("Tried to access block but there are not any.");

    fileStream.Position = resourceInfo.dataOffset;
    byte[] rawData = binaryReader.ReadBytes(directoryEntry.LengthPacked);
    byte[] blockData = ReadBlock(rawData, directoryEntry, blockIndex);

    using MemoryStream ms = new(blockData);
    using BinaryReader msbr = new(ms);

    int structSize = Marshal.SizeOf(typeof(T));

    if (structSize > ms.Length || ms.Length % structSize != 0)
      throw new ArgumentException($"Chunk length {ms.Length} is not divisible by struct {typeof(T)} size {structSize}.");

    T[] structs = new T[ms.Length / structSize];

    for (int i = 0; i < structs.Length; ++i)
      structs[i] = msbr.Read<T>();

    return structs;
  }

  public ushort GetResourceBlockCount(ushort resourceId) => GetResourceBlockCount(resourceEntries[resourceId]);
  public ushort GetResourceBlockCount(ResourceInfo resourceInfo) {
    DirectoryEntry directoryEntry = resourceInfo.info;
    if (directoryEntry.Flags.HasFlag(ResourceFlags.Compound)) {
      fileStream.Position = resourceInfo.dataOffset;
      return binaryReader.ReadUInt16();
    } else {
      return 1;
    }
  }

  private byte[] ReadBlock(byte[] rawData, DirectoryEntry directoryEntry, ushort blockIndex = 0) {
    if (directoryEntry.Flags.HasFlag(ResourceFlags.Compound)) {
      using MemoryStream ms = new(rawData);
      using BinaryReader msbr = new(ms);

      ushort blockCount = msbr.ReadUInt16();

      if (blockIndex >= blockCount)
        throw new ArgumentOutOfRangeException(nameof(blockIndex), $"Resource has only {blockCount} blocks");

      ms.Position += blockIndex * sizeof(int);
      int blockStart = msbr.ReadInt32();
      int blockEnd = msbr.ReadInt32();

      if (directoryEntry.Flags.HasFlag(ResourceFlags.LZW)) {
        ms.Position = sizeof(ushort) + (blockCount + 1) * sizeof(int);  // move to start of data
        return Unpack(msbr, blockEnd - blockStart, blockStart);
      } else {
        ms.Position = blockStart;
        return msbr.ReadBytes(blockEnd - blockStart);
      }
    } else if (directoryEntry.Flags.HasFlag(ResourceFlags.LZW)) {
      using MemoryStream ms = new(rawData);
      using BinaryReader msbr = new(ms);
      return Unpack(msbr, directoryEntry.LengthUnpacked);
    } else {
      return rawData;
    }
  }

  private byte[][] ReadBlock(byte[] rawData, DirectoryEntry directoryEntry) {
    if (directoryEntry.Flags.HasFlag(ResourceFlags.Compound)) {
      using MemoryStream ms = new(rawData);
      using BinaryReader msbr = new(ms);

      ushort blockCount = msbr.ReadUInt16();
      long blockDirectory = ms.Position;

      byte[][] blockDatas = new byte[blockCount][];

      if (directoryEntry.Flags.HasFlag(ResourceFlags.LZW)) {
        int headerSize = sizeof(ushort) + (blockCount + 1) * sizeof(int);

        ms.Position = headerSize;
        byte[] unpackedData = Unpack(msbr, directoryEntry.LengthUnpacked - headerSize);

        using MemoryStream ums = new(unpackedData);
        using BinaryReader umsbr = new(ms);

        for (int i = 0; i < blockCount; ++i) {
          ms.Position = blockDirectory + (i * sizeof(int));
          int blockStart = msbr.ReadInt32();
          int blockEnd = msbr.ReadInt32();

          ums.Position = blockStart - headerSize;
          blockDatas[i] = umsbr.ReadBytes(blockEnd - blockStart);
        }
      } else {
        for (int i = 0; i < blockCount; ++i) {
          ms.Position = blockDirectory + (i * sizeof(int));
          int blockStart = msbr.ReadInt32();
          int blockEnd = msbr.ReadInt32();

          ms.Position = blockStart;
          blockDatas[i] = msbr.ReadBytes(blockEnd - blockStart);
        }
      }

      return blockDatas;
    } else if (directoryEntry.Flags.HasFlag(ResourceFlags.LZW)) {
      using MemoryStream ms = new(rawData);
      using BinaryReader msbr = new(ms);
      return new byte[][] { Unpack(msbr, directoryEntry.LengthUnpacked) };
    } else {
      return new byte[][] { rawData };
    }
  }

  private byte[] Unpack(BinaryReader rmsbr, int unpackBytes, int skipBytes = 0) {
    const ushort KeyEndOfStream = 0x3FFF;
    const ushort KeyResetDictionary = 0x3FFE;
    const ushort MaxReferenceWords = KeyResetDictionary - 0x00FF;

    byte[] blockData = new byte[unpackBytes];

    long[] offset = new long[MaxReferenceWords];
    short[] reference = new short[MaxReferenceWords];
    ushort[] unpackedLength = new ushort[MaxReferenceWords];

    for (int i = 0; i < MaxReferenceWords; ++i) {
      unpackedLength[i] = 1;
      reference[i] = -1;
    }

    using (MemoryStream bms = new(blockData)) {
      BinaryWriter bmsbw = new(bms);

      int bits = 0;
      ulong bitBuffer = 0;
      ushort wordIndex = 0;

      while (bms.Position < unpackBytes) {
        while (bits < 14) {
          bitBuffer = (bitBuffer << 8) | rmsbr.ReadByte(); // move buffer 8 bits to left, insert 8 bits to buffer
          bits += 8; // added 8 bits.
        }

        bits -= 14; // consume 14 bits.
        ushort value = (ushort)((bitBuffer >> bits) & 0x3FFF); // shift right to ignore unconsumed bits

        if (value == KeyEndOfStream) {
          break;
        } else if (value == KeyResetDictionary) {
          for (int i = 0; i < MaxReferenceWords; ++i) {
            unpackedLength[i] = 1;
            reference[i] = -1;
          }
          wordIndex = 0;
        } else {
          if (wordIndex < MaxReferenceWords) {
            offset[wordIndex] = bms.Position; // set unpacked data position to wordIndex

            if (value >= 0x0100) // value is index to reference word
              reference[wordIndex] = (short)(value - 0x0100);
          }

          ++wordIndex;

          if (value < 0x0100) { // byte value
            if (skipBytes > 0) { --skipBytes; continue; }

            bmsbw.Write((byte)(value & 0xFF));
          } else { // check dictionary
            value -= 0x0100;

            if (unpackedLength[value] == 1) { // First time looking for reference
              if (reference[value] != -1) // reference found in dictionary
                unpackedLength[value] += unpackedLength[reference[value]]; // add length of referenced byte sequence
              else // reference not found in dictionary
                unpackedLength[value] += 1; // increase length by one to read next uncompressed byte
            }

            for (int i = 0; i < unpackedLength[value] && bms.Position < unpackBytes; ++i) {
              if (skipBytes > 0) { --skipBytes; continue; }

              bmsbw.Write(blockData[i + offset[value]]);
            }
          }
        }
      }
    }

    return blockData;
  }

  public void Dispose() {
    binaryReader.Close();
    fileStream.Close();

    binaryReader.Dispose();
    fileStream.Dispose();
  }

  public enum ContentType : byte {
    Palette,
    String,
    Image,
    Font,
    Animation,
    Voc = 0x07,
    Obj3D = 0x0F,
    Movie = 0x11,
    Map = 0x30
  }

  [Flags]
  public enum ResourceFlags : byte {
    LZW = 0x01,
    Compound = 0x02,
    LoadOnOpen = 0x08
  }
  
  [StructLayout(LayoutKind.Sequential, Pack = 1)]
  public struct FileHeader {
    public const string FILE_SIGNATURE = "LG Res File v2\r\n";
    public const int SIGNATURE_LENGTH = 16;
    public const int COMMENT_LENGTH = 96;
    private const byte CTRL_Z = 26;
    
    public unsafe fixed byte Signature[SIGNATURE_LENGTH];
    public unsafe fixed byte Comment[COMMENT_LENGTH];
    public unsafe fixed byte Reserved[12];
    public uint DirectoryOffset;

    public unsafe void SetSignature() {
      var signatureString = Encoding.ASCII.GetBytes(FILE_SIGNATURE);
      fixed (byte* src = &signatureString[0], trg = Signature) {
        Buffer.MemoryCopy(src, trg, SIGNATURE_LENGTH, SIGNATURE_LENGTH);
      }
    }
    
    public unsafe void SetComment(string comment) {
      var commentBytes = Encoding.ASCII.GetBytes(comment);
      fixed (byte* src = &commentBytes[0], trg = Comment) {
        Buffer.MemoryCopy(src, trg, COMMENT_LENGTH - 1, comment.Length);
      }
      
      Comment[comment.Length] = CTRL_Z;
    }
    
    public unsafe string GetSignature() {
      fixed (byte* signature = Signature) {
        return Encoding.ASCII.GetString(signature, SIGNATURE_LENGTH);
      }
    }
  }
  
  [StructLayout(LayoutKind.Sequential, Pack = 1)]
  public struct DirectoryHeader {
    /**
     * Number of resources in directory
     */
    public ushort NumberOfEntries;
    /**
     * File offset to beginning of first resource data
     */
    public uint DataOffset;
  }

  [StructLayout(LayoutKind.Sequential, Pack = 1)]
  public struct DirectoryEntry {
    public ushort Id;

    private unsafe fixed byte lengthUnpacked[3];

    public ResourceFlags Flags;

    private unsafe fixed byte lengthPacked[3];

    public ContentType ContentType;

    public unsafe int LengthUnpacked {
      readonly get => lengthUnpacked[0] | lengthUnpacked[1] << 8 | lengthUnpacked[2] << 16;
      init {
        lengthUnpacked[0] = (byte)value;
        lengthUnpacked[1] = (byte)(value >> 8);
        lengthUnpacked[2] = (byte)(value >> 16);
      }
    }

    public unsafe int LengthPacked {
      readonly get => lengthPacked[0] | lengthPacked[1] << 8 | lengthPacked[2] << 16;
      init {
        lengthPacked[0] = (byte)value;
        lengthPacked[1] = (byte)(value >> 8);
        lengthPacked[2] = (byte)(value >> 16);
      }
    } 

    public readonly override string ToString() => $"Id = {Id}, LengthUnpacked = {LengthUnpacked}, Flags = {Flags}, LengthPacked = {LengthPacked}, ContentType = {ContentType}";
  }

  public struct ResourceInfo {
    public DirectoryEntry info;
    public long dataOffset;
  }
}
