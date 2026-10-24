using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Kudusync.Core
{
    /// <summary>An object read from a .NET binary-serialized file: its class name and its fields.</summary>
    public sealed class NrbfObject
    {
        public string ClassName { get; set; }
        public Dictionary<string, object> Members { get; } = new Dictionary<string, object>();

        /// <summary>A field by name, ignoring case and the usual decorations ("_name", "m_name", "&lt;Name&gt;k__BackingField").</summary>
        public object Get(string name)
        {
            foreach (var pair in Members)
                if (string.Equals(Plain(pair.Key), name, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            return null;
        }

        public static string Plain(string member)
        {
            var m = member ?? string.Empty;
            if (m.StartsWith("<") && m.Contains(">k__BackingField")) m = m.Substring(1, m.IndexOf('>') - 1);
            if (m.StartsWith("m_")) m = m.Substring(2);
            return m.TrimStart('_');
        }

        public override string ToString() => ClassName;
    }

    /// <summary>
    /// A reader for the .NET Remoting Binary Format ([MS-NRBF], publicly documented by Microsoft). It only reads data:
    /// no types are loaded and no code runs, so it's safe on any file. Used to import SyncToy's folder pairs.
    /// </summary>
    public sealed class NrbfReader
    {
        private sealed class ClassInfo
        {
            public string Name;
            public string[] MemberNames;
            public byte[] BinaryTypes; // null when the members aren't typed
            public object[] Extra;
        }

        private sealed class Ref
        {
            public int Id;
        }

        private sealed class NullRun
        {
            public int Count;
        }

        private readonly BinaryReader _r;
        private readonly Dictionary<int, object> _objects = new Dictionary<int, object>();
        private readonly Dictionary<int, ClassInfo> _classes = new Dictionary<int, ClassInfo>();

        private NrbfReader(Stream stream) => _r = new BinaryReader(stream, Encoding.UTF8);

        /// <summary>Reads every serialized object in the stream (some programs write several one after another) and returns their roots.</summary>
        public static List<object> ReadAll(Stream stream)
        {
            var roots = new List<object>();
            while (stream.Position < stream.Length)
            {
                var reader = new NrbfReader(stream);
                roots.Add(reader.ReadOne());
            }
            return roots;
        }

        private object ReadOne()
        {
            var type = _r.ReadByte();
            if (type != 0) throw new InvalidDataException("This isn't a .NET binary-serialized file.");
            var rootId = _r.ReadInt32();
            _r.ReadInt32(); // header id
            var major = _r.ReadInt32();
            _r.ReadInt32();
            if (major != 1) throw new InvalidDataException("Unsupported serialization format version.");

            while (true)
            {
                var value = ReadRecord(out var end);
                if (end) break;
                if (value is NullRun) continue;
            }
            Resolve();
            return _objects.TryGetValue(rootId, out var root) ? root : null;
        }

        private object ReadRecord(out bool end)
        {
            end = false;
            var type = _r.ReadByte();
            switch (type)
            {
                case 1: // ClassWithId
                {
                    var id = _r.ReadInt32();
                    var metadataId = _r.ReadInt32();
                    if (!_classes.TryGetValue(metadataId, out var info)) throw new InvalidDataException("Unknown class reference.");
                    return ReadObject(id, info);
                }
                case 2: // SystemClassWithMembers
                {
                    var (id, info) = ReadClassInfo();
                    return ReadObject(id, info);
                }
                case 3: // ClassWithMembers
                {
                    var (id, info) = ReadClassInfo();
                    _r.ReadInt32(); // library id
                    return ReadObject(id, info);
                }
                case 4: // SystemClassWithMembersAndTypes
                {
                    var (id, info) = ReadClassInfo();
                    ReadMemberTypes(info);
                    return ReadObject(id, info);
                }
                case 5: // ClassWithMembersAndTypes
                {
                    var (id, info) = ReadClassInfo();
                    ReadMemberTypes(info);
                    _r.ReadInt32(); // library id
                    return ReadObject(id, info);
                }
                case 6: // BinaryObjectString
                {
                    var id = _r.ReadInt32();
                    var s = _r.ReadString();
                    _objects[id] = s;
                    return s;
                }
                case 7: // BinaryArray
                {
                    var id = _r.ReadInt32();
                    var arrayType = _r.ReadByte();
                    var rank = _r.ReadInt32();
                    var lengths = new int[rank];
                    for (var i = 0; i < rank; i++) lengths[i] = _r.ReadInt32();
                    if (arrayType >= 3 && arrayType <= 5)
                        for (var i = 0; i < rank; i++) _r.ReadInt32(); // lower bounds
                    var elementType = _r.ReadByte();
                    var extra = ReadExtra(elementType);
                    var total = lengths.Aggregate(1L, (a, b) => a * b);
                    if (total < 0 || total > 10_000_000) throw new InvalidDataException("Array too large.");
                    var items = elementType == 0
                        ? Enumerable.Range(0, (int)total).Select(_ => ReadPrimitive((byte)extra)).ToArray()
                        : ReadElements((int)total);
                    _objects[id] = items;
                    return items;
                }
                case 8: // MemberPrimitiveTyped
                    return ReadPrimitive(_r.ReadByte());
                case 9: // MemberReference
                    return new Ref { Id = _r.ReadInt32() };
                case 10: // ObjectNull
                    return null;
                case 11: // MessageEnd
                    end = true;
                    return null;
                case 12: // BinaryLibrary
                    _r.ReadInt32();
                    _r.ReadString();
                    return ReadRecord(out end); // a library record comes just before the record that uses it
                case 13: // ObjectNullMultiple256
                    return new NullRun { Count = _r.ReadByte() };
                case 14: // ObjectNullMultiple
                    return new NullRun { Count = _r.ReadInt32() };
                case 15: // ArraySinglePrimitive
                {
                    var id = _r.ReadInt32();
                    var length = _r.ReadInt32();
                    var primitive = _r.ReadByte();
                    if (length < 0 || length > 100_000_000) throw new InvalidDataException("Array too large.");
                    object items;
                    if (primitive == 2) items = _r.ReadBytes(length);
                    else items = Enumerable.Range(0, length).Select(_ => ReadPrimitive(primitive)).ToArray();
                    _objects[id] = items;
                    return items;
                }
                case 16: // ArraySingleObject
                case 17: // ArraySingleString
                {
                    var id = _r.ReadInt32();
                    var length = _r.ReadInt32();
                    if (length < 0 || length > 10_000_000) throw new InvalidDataException("Array too large.");
                    var items = ReadElements(length);
                    _objects[id] = items;
                    return items;
                }
                default:
                    throw new InvalidDataException("Unsupported record type " + type + ".");
            }
        }

        private object[] ReadElements(int count)
        {
            var items = new object[count];
            for (var i = 0; i < count;)
            {
                var value = ReadRecord(out var end);
                if (end) throw new InvalidDataException("The file ended too early.");
                if (value is NullRun run)
                {
                    i += run.Count;
                    continue;
                }
                items[i++] = value;
            }
            return items;
        }

        private (int Id, ClassInfo Info) ReadClassInfo()
        {
            var id = _r.ReadInt32();
            var info = new ClassInfo { Name = _r.ReadString() };
            var count = _r.ReadInt32();
            if (count < 0 || count > 10000) throw new InvalidDataException("Too many members.");
            info.MemberNames = new string[count];
            for (var i = 0; i < count; i++) info.MemberNames[i] = _r.ReadString();
            _classes[id] = info;
            return (id, info);
        }

        private void ReadMemberTypes(ClassInfo info)
        {
            var count = info.MemberNames.Length;
            info.BinaryTypes = _r.ReadBytes(count);
            info.Extra = new object[count];
            for (var i = 0; i < count; i++) info.Extra[i] = ReadExtra(info.BinaryTypes[i]);
        }

        private object ReadExtra(byte binaryType)
        {
            switch (binaryType)
            {
                case 0: // Primitive
                case 7: // PrimitiveArray
                    return _r.ReadByte();
                case 3: // SystemClass
                    return _r.ReadString();
                case 4: // Class
                    var name = _r.ReadString();
                    _r.ReadInt32();
                    return name;
                default:
                    return null;
            }
        }

        private NrbfObject ReadObject(int id, ClassInfo info)
        {
            var obj = new NrbfObject { ClassName = info.Name };
            _objects[id] = obj;
            for (var i = 0; i < info.MemberNames.Length; i++)
            {
                object value;
                if (info.BinaryTypes != null && info.BinaryTypes[i] == 0) value = ReadPrimitive((byte)info.Extra[i]);
                else
                {
                    value = ReadRecord(out var end);
                    if (end) throw new InvalidDataException("The file ended too early.");
                    if (value is NullRun) value = null;
                }
                obj.Members[info.MemberNames[i]] = value;
            }
            return obj;
        }

        private object ReadPrimitive(byte type)
        {
            switch (type)
            {
                case 1: return _r.ReadBoolean();
                case 2: return _r.ReadByte();
                case 3: return ReadUtf8Char();
                case 5: return _r.ReadString(); // decimal, as text
                case 6: return _r.ReadDouble();
                case 7: return _r.ReadInt16();
                case 8: return _r.ReadInt32();
                case 9: return _r.ReadInt64();
                case 10: return _r.ReadSByte();
                case 11: return _r.ReadSingle();
                case 12: return new TimeSpan(_r.ReadInt64());
                case 13: return new DateTime(_r.ReadInt64() & 0x3FFFFFFFFFFFFFFF);
                case 14: return _r.ReadUInt16();
                case 15: return _r.ReadUInt32();
                case 16: return _r.ReadUInt64();
                case 17: return null;
                case 18: return _r.ReadString();
                default: throw new InvalidDataException("Unsupported value type " + type + ".");
            }
        }

        private char ReadUtf8Char()
        {
            var first = _r.ReadByte();
            var extra = first >= 0xF0 ? 3 : first >= 0xE0 ? 2 : first >= 0xC0 ? 1 : 0;
            var bytes = new byte[1 + extra];
            bytes[0] = first;
            for (var i = 1; i <= extra; i++) bytes[i] = _r.ReadByte();
            var s = Encoding.UTF8.GetString(bytes);
            return s.Length > 0 ? s[0] : '\0';
        }

        /// <summary>Replaces references with the objects they point to.</summary>
        private void Resolve()
        {
            object Fix(object v) => v is Ref r && _objects.TryGetValue(r.Id, out var target) ? target : v is Ref ? null : v;
            foreach (var o in _objects.Values.ToList())
            {
                if (o is NrbfObject obj)
                    foreach (var key in obj.Members.Keys.ToList()) obj.Members[key] = Fix(obj.Members[key]);
                else if (o is object[] array)
                    for (var i = 0; i < array.Length; i++) array[i] = Fix(array[i]);
            }
        }
    }
}
