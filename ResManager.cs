using System.Runtime.InteropServices;
using System.Text;

namespace SystemShockPatcher;

public static class ResManager {
    private static readonly Dictionary<ushort, ResourceData> resourceRecord = new();
    
    public static void Read (string filePath) {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        
        byte[] rawBytes = new byte[stream.Length];
        
        {
            int remaining = (int)stream.Length;
            int offset = 0;

            while (remaining != 0) {
                var bytesRead = stream.Read(rawBytes, offset, remaining);

                if (bytesRead == 0)
                    break;

                offset += bytesRead;
                remaining -= bytesRead;
            }
        }

        var resFile = new ResourceFile(rawBytes);
        
        foreach (var (resId, resource) in resFile.ResourceEntries) {
            if (resId < 3) {
                Console.WriteLine($"Warning ({filePath}): Skipping resource with id {resId}.");
                continue;
            }

            var chunkData = resFile.GetResourceData(resId);
            var flags = resource.info.Flags;

            if (resource.info.Flags.HasFlag(ResourceFile.ResourceFlags.LZW)) {
                Console.WriteLine($"Warning ({filePath}/{resId:X4}): LZW Packing unsupported. Will write output as unpacked.");
                flags &= ~ResourceFile.ResourceFlags.LZW;
            }

            if (resourceRecord.TryGetValue(resId, out var prevResource)) {
                if (resource.info.ContentType != prevResource.Info.ContentType)
                    Console.WriteLine($"Warning ({filePath}/{resId:X4}): Content types do not match old: {prevResource.Info.ContentType} new: {resource.info.ContentType }. Will use new content type.");
                
                /*
                 * Merge
                 * https://github.com/inkyblackness/hacked/wiki/ModdingSupport
                 */
                for (var chunkEntryIndex = 0; chunkEntryIndex < chunkData.Length; ++chunkEntryIndex) {
                    var newEntryData = chunkData[chunkEntryIndex];

                    if (newEntryData.Length == 0) { // Don't override
                        if (chunkEntryIndex < prevResource.ChunkData.Length)
                            chunkData[chunkEntryIndex] = prevResource.ChunkData[chunkEntryIndex]; // Use old data
                        
                        continue;
                    }

                    Console.WriteLine($"{filePath}: Patching {resId:X4}/{chunkEntryIndex} type: {resource.info.ContentType}");
                }
            } else {
                Console.WriteLine($"{filePath}: Adding {resId:X4} type: {resource.info.ContentType}");
            }
            
            var totalLength = chunkData.Aggregate(0, (total, entryData) => total + entryData.Length);
            if (resource.info.Flags.HasFlag(ResourceFile.ResourceFlags.Compound)) {
                // TODO FIXME Calculates header size here. This should be handled when Compound header is written...
                
                ushort blockCount = (ushort)chunkData.Length;
                int headerSize = sizeof(ushort) + (blockCount + 1) * sizeof(int);
                totalLength += headerSize;
            }

            resourceRecord[resId] = new ResourceData() {
                Info = new ResourceFile.DirectoryEntry() {
                    Id = resId,
                    LengthUnpacked = totalLength,
                    Flags = flags,
                    LengthPacked = totalLength,
                    ContentType = resource.info.ContentType
                },
                ChunkData = chunkData
            };
        }
    }

    public static void Save(string filePath) {
        using var stream = File.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var binaryWriter = new BinaryWriter(stream, Encoding.ASCII);

        // https://github.com/inkyblackness/ss-specs/blob/main/fileFormat/ResourceFiles.md
        
        var header = new ResourceFile.FileHeader();
        header.SetSignature();
        header.SetComment("Built with SystemShockPatcher");
        
        binaryWriter.Write(header);
        var dataStart = stream.Position;

        #region Resources
        foreach (var (resId, resource) in resourceRecord) {
            if (resource.Info.Flags.HasFlag(ResourceFile.ResourceFlags.Compound)) {
                // Write Compound header

                ushort blockCount = (ushort)resource.ChunkData.Length;
                binaryWriter.Write(blockCount);
                
                uint headerSize = (uint)(sizeof(ushort) + (blockCount + 1) * sizeof(int));
                
                uint offset = headerSize;
                binaryWriter.Write(offset);
                
                foreach (var entryData in resource.ChunkData) {
                    offset += (uint)entryData.Length;
                    binaryWriter.Write(offset);
                }
            }

            foreach (var entryData in resource.ChunkData) {
                stream.Write(entryData);
            }
            
            var alignment = (4 - resource.Info.LengthPacked) & 0x3;
            while (alignment-- > 0)
                binaryWriter.Write((byte)0);
        }
        #endregion

        var dirStart = stream.Position;
        
        #region Directory
        binaryWriter.Write(new ResourceFile.DirectoryHeader() {
            NumberOfEntries = (ushort)resourceRecord.Count,
            DataOffset = (uint)dataStart
        });

        foreach (var (resId, resource) in resourceRecord) {
            binaryWriter.Write(resource.Info);
        }
        #endregion

        #region File header
        header.DirectoryOffset = (uint)dirStart;
        
        stream.Position = 0;
        binaryWriter.Write(header);
        #endregion
    }
    
    public struct ResourceData {
        public ResourceFile.DirectoryEntry Info;
        public byte[][] ChunkData;
    }
}