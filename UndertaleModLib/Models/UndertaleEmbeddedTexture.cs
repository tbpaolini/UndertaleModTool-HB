﻿using ICSharpCode.SharpZipLib.BZip2;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UndertaleModLib.Util;

namespace UndertaleModLib.Models
{
    [PropertyChanged.AddINotifyPropertyChangedInterface]
    public class UndertaleEmbeddedTexture : UndertaleNamedResource
    {
        public UndertaleString Name { get; set; }
        public uint Scaled { get; set; } = 0;
        public uint GeneratedMips { get; set; }
        public uint UnknownTextureProperty { get; set; } = 0;
        public TexData TextureData { get; set; } = new TexData();

        public void Serialize(UndertaleWriter writer)
        {
            writer.Write(Scaled);
            if (writer.undertaleData.GeneralInfo.Major >= 2)
                writer.Write(GeneratedMips);
            writer.Write(UnknownTextureProperty);
            writer.WriteUndertaleObjectPointer(TextureData);
        }

        public void Unserialize(UndertaleReader reader)
        {
            Scaled = reader.ReadUInt32();
            if (reader.undertaleData.GeneralInfo.Major >= 2)
                GeneratedMips = reader.ReadUInt32();
            // Heartbound fix: A new (and unknown) 4 bytes value is present at the texture's header.
            // So it is being skipped in order to prevent misalignment.
            UnknownTextureProperty = reader.ReadUInt32();
            // End of the fix
            TextureData = reader.ReadUndertaleObjectPointer<TexData>();
        }

        public void SerializeBlob(UndertaleWriter writer)
        {
            // padding
            while (writer.Position % 0x80 != 0)
                writer.Write((byte)0);

            writer.WriteUndertaleObject(TextureData);
        }

        public void UnserializeBlob(UndertaleReader reader)
        {
            while (reader.Position % 0x80 != 0)
                if (reader.ReadByte() != 0)
                    throw new IOException("Padding error!");

            reader.ReadUndertaleObject(TextureData);
        }

        public override string ToString()
        {
            if (Name != null)
                return Name.Content + " (" + GetType().Name + ")";
            else
                Name = new UndertaleString("Texture Unknown Index");
            return Name.Content + " (" + GetType().Name + ")";
        }

        public class TexData : UndertaleObject, INotifyPropertyChanged
        {
            private byte[] _TextureBlob;

            public byte[] TextureBlob { get => _TextureBlob; set { _TextureBlob = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextureBlob))); } }

            public event PropertyChangedEventHandler PropertyChanged;

            private static readonly byte[] PNGHeader = new byte[8] { 137, 80, 78, 71, 13, 10, 26, 10 };
            private static readonly byte[] QOIandBZipHeader = new byte[4] { 50, 122, 111, 113 };
            private static readonly byte[] QOIHeader = new byte[4] { 102, 105, 111, 113 };

            public void Serialize(UndertaleWriter writer)
            {
                if (writer.undertaleData.UseQoiFormat)
                {
                    if (writer.undertaleData.UseBZipFormat)
                    {
                        writer.Write(QOIandBZipHeader);

                        // Encode the PNG data back to QOI+BZip2
                        Bitmap bmp = TextureWorker.GetImageFromByteArray(TextureBlob);
                        writer.Write((short)bmp.Width);
                        writer.Write((short)bmp.Height);
                        byte[] data = QoiConverter.GetArrayFromImage(bmp);
                        using MemoryStream input = new MemoryStream(data);
                        using MemoryStream output = new MemoryStream(1024);
                        BZip2.Compress(input, output, false, 9);
                        // Heartbound FIX: Write the uncompressed data size before the data itself
                        writer.Write((uint)data.Length);
                        // End of the fix
                        writer.Write(output.ToArray());
                        bmp.Dispose();
                    }
                    else
                    {
                        // Encode the PNG data back to QOI
                        writer.Write(QoiConverter.GetArrayFromImage(TextureWorker.GetImageFromByteArray(TextureBlob)));
                    }
                }
                else
                    writer.Write(TextureBlob);
            }

            public void Unserialize(UndertaleReader reader)
            {
                uint startAddress = reader.Position;

                byte[] header = reader.ReadBytes(8);
                if (!header.SequenceEqual(PNGHeader))
                {
                    reader.Position = startAddress;

                    if (header.Take(4).SequenceEqual(QOIandBZipHeader))
                    {
                        reader.undertaleData.UseQoiFormat = true;
                        reader.undertaleData.UseBZipFormat = true;

                        // Don't really care about the width/height, so skip them, as well as header
                        reader.Position += 8;

                        // Need to fully decompress and convert the QOI data to PNG for compatibility purposes (at least for now)
                        using MemoryStream bufferWrapper = new MemoryStream(reader.Buffer);
                        bufferWrapper.Seek(reader.Offset+4, SeekOrigin.Begin);  // Heartbound fix: The file started 4 bytes before where it should, so 4 is being added to the offset.
                        using MemoryStream result = new MemoryStream(1024);
                        BZip2.Decompress(bufferWrapper, result, false);
                        reader.Position = (uint)bufferWrapper.Position;
                        result.Seek(0, SeekOrigin.Begin);
                        Bitmap bmp = QoiConverter.GetImageFromStream(result);
                        using MemoryStream final = new MemoryStream();
                        bmp.Save(final, ImageFormat.Png);
                        TextureBlob = final.ToArray();
                        bmp.Dispose();
                        return;
                    }
                    else if (header.Take(4).SequenceEqual(QOIHeader))
                    {
                        reader.undertaleData.UseQoiFormat = true;
                        reader.undertaleData.UseBZipFormat = false;

                        // Need to convert the QOI data to PNG for compatibility purposes (at least for now)
                        using MemoryStream ms = new MemoryStream(reader.Buffer);
                        ms.Seek(reader.Offset, SeekOrigin.Begin);
                        Bitmap bmp = QoiConverter.GetImageFromStream(ms);
                        reader.Offset = (int)ms.Position;
                        using MemoryStream final = new MemoryStream();
                        bmp.Save(final, ImageFormat.Png);
                        TextureBlob = final.ToArray();
                        bmp.Dispose();
                        return;
                    }
                    else
                        throw new IOException("Didn't find PNG or QOI+BZip2 header");
                }

                // There is no length for the PNG anywhere as far as I can see
                // The only thing we can do is parse the image to find the end
                while (true)
                {
                    // PNG is big endian and BinaryRead can't handle that (damn)
                    uint len = (uint)reader.ReadByte() << 24 | (uint)reader.ReadByte() << 16 | (uint)reader.ReadByte() << 8 | (uint)reader.ReadByte();
                    string type = Encoding.UTF8.GetString(reader.ReadBytes(4));
                    reader.Position += len + 4;
                    if (type == "IEND")
                        break;
                }

                uint length = reader.Position - startAddress;
                reader.Position = startAddress;
                TextureBlob = reader.ReadBytes((int)length);
            }
        }
    }
}
