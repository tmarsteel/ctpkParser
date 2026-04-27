using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using ProtoBuf;

namespace ctpkLib
{
    public static class SQLConverter
    {
        public static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("Usage: ctpkTools <ctpk-file> [output.sql]");
                Environment.Exit(1);
            }

            CTPKLib lib = new CTPKLib(new FileStream(args[0], FileMode.Open));

            if (args.Length >= 2)
            {
                using (var writer = new StreamWriter(args[1]))
                    WriteSQL(lib, writer);
            }
            else
            {
                WriteSQL(lib, Console.Out);
            }
        }

        public static void WriteSQL(CTPKLib lib, TextWriter writer)
        {
            var sectionTableMap = GetTypedSectionTypes()
                .ToDictionary(t => t.GetCustomAttribute<Section>().Id, GetTableName);
            WriteSchemaSQL(writer, sectionTableMap);
            WriteDataSQL(lib, writer, sectionTableMap);
        }

        private static void WriteSchemaSQL(TextWriter writer, Dictionary<uint, string> sectionTableMap)
        {
            foreach (var objType in GetTypedSectionTypes().OrderBy(GetTableName))
            {
                var mapType = GetMapType(objType);
                if (mapType == null) continue;

                var fields = GetDataFields(mapType);
                var colNames = ComputeColumnNames(fields, sectionTableMap);
                var sectionId = objType.GetCustomAttribute<Section>().Id;
                var idField = mapType.GetField("field_1", BindingFlags.Public | BindingFlags.Instance);

                var fks = fields
                    .Zip(colNames, (f, n) => new { field = f, colName = n })
                    .Select(x => new { x.colName, attr = x.field.GetCustomAttribute<MappedObject>() })
                    .Where(x => x.attr != null && sectionTableMap.ContainsKey(x.attr.SectionId))
                    .Select(x => new { x.colName, refTable = sectionTableMap[x.attr.SectionId] })
                    .ToList();

                writer.WriteLine($"CREATE TABLE IF NOT EXISTS [{GetTableName(objType)}] ( -- 0x{sectionId:X8}");

                string idProto = idField != null ? $" -- proto {GetProtoTag(idField)}" : "";
                writer.WriteLine($"  [id] INTEGER PRIMARY KEY{(fields.Length > 0 || fks.Count > 0 ? "," : "")}{idProto}");

                for (int i = 0; i < fields.Length; i++)
                {
                    bool isLast = i == fields.Length - 1 && fks.Count == 0;
                    int tag = GetProtoTag(fields[i]);
                    string proto = tag >= 0 ? $" -- proto {tag}" : "";
                    writer.WriteLine($"  [{colNames[i]}] {GetSqlType(fields[i])}{(isLast ? "" : ",")}{proto}");
                }

                for (int i = 0; i < fks.Count; i++)
                {
                    bool isLast = i == fks.Count - 1;
                    writer.WriteLine($"  FOREIGN KEY ([{fks[i].colName}]) REFERENCES [{fks[i].refTable}]([id]){(isLast ? "" : ",")}");
                }

                writer.WriteLine(");");
                writer.WriteLine();
            }
        }

        private static void WriteDataSQL(CTPKLib lib, TextWriter writer, Dictionary<uint, string> sectionTableMap)
        {
            var sectionTypeMap = GetTypedSectionTypes()
                .ToDictionary(t => t.GetCustomAttribute<Section>().Id);

            foreach (var kvp in lib.Objects.ObjectMap)
            {
                if (!sectionTypeMap.TryGetValue(kvp.Key, out var objType)) continue;

                var mapType = GetMapType(objType);
                if (mapType == null) continue;

                var idField = mapType.GetField("field_1", BindingFlags.Public | BindingFlags.Instance);
                var fields = GetDataFields(mapType);
                if (idField == null && fields.Length == 0) continue;

                var colNames = ComputeColumnNames(fields, sectionTableMap);

                string tableName = GetTableName(objType);
                string colList = "[id]" + (fields.Length > 0 ? ", " + string.Join(", ", colNames.Select(n => $"[{n}]")) : "");

                foreach (var obj in kvp.Value)
                {
                    if (obj.Map.GetType() == typeof(ObjMap)) continue;

                    string idVal = idField != null ? idField.GetValue(obj.Map).ToString() : obj.Id.ToString();
                    var vals = new List<string> { idVal };
                    foreach (var field in fields)
                        vals.Add(GetSqlValue(lib, obj, field));

                    writer.WriteLine($"INSERT INTO [{tableName}] ({colList}) VALUES ({string.Join(", ", vals)});");
                }
            }
        }

        private static FieldInfo[] GetDataFields(Type mapType)
        {
            return mapType.GetFields(BindingFlags.Public | BindingFlags.Instance)
                          .Where(f => f.Name != "field_1")
                          .ToArray();
        }

        private static string[] ComputeColumnNames(FieldInfo[] fields, Dictionary<uint, string> sectionTableMap)
        {
            var baseCounts = new Dictionary<string, int>();
            foreach (var field in fields)
            {
                var attr = field.GetCustomAttribute<MappedObject>();
                if (attr != null && IsHexFieldName(field.Name) && sectionTableMap.TryGetValue(attr.SectionId, out var refTable))
                {
                    string key = refTable + "_id";
                    baseCounts[key] = baseCounts.TryGetValue(key, out int c) ? c + 1 : 1;
                }
            }

            var names = new string[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                var attr = field.GetCustomAttribute<MappedObject>();
                string name;

                if (attr != null && IsHexFieldName(field.Name) && sectionTableMap.TryGetValue(attr.SectionId, out var refTable))
                {
                    string key = refTable + "_id";
                    name = baseCounts[key] > 1 ? $"{refTable}_id_{GetProtoTag(field)}" : key;
                }
                else
                {
                    name = field.Name;
                }

                names[i] = name;
            }

            return names;
        }

        private static bool IsHexFieldName(string name)
        {
            if (!name.StartsWith("field_") || name.Length <= 6) return false;
            foreach (char c in name.Substring(6))
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return false;
            return true;
        }

        private static int GetProtoTag(FieldInfo field)
        {
            return field.GetCustomAttribute<ProtoMemberAttribute>()?.Tag ?? -1;
        }

        private static string GetSqlValue(CTPKLib lib, CatalogueObject obj, FieldInfo field)
        {
            object raw = field.GetValue(obj.Map);

            if (Attribute.IsDefined(field, typeof(MappedString)))
            {
                uint hash = (uint)raw;
                if (hash == 0) return "NULL";
                return lib.Strings.StringMap.TryGetValue(hash, out var str)
                    ? "'" + str.Replace("'", "''") + "'"
                    : "NULL";
            }

            if (Attribute.IsDefined(field, typeof(MappedObject)))
            {
                uint id = (uint)raw;
                return id == 0 ? "NULL" : id.ToString();
            }

            if (field.FieldType == typeof(bool))
                return (bool)raw ? "1" : "0";

            if (field.FieldType == typeof(float))
                return ((float)raw).ToString("R", CultureInfo.InvariantCulture);

            return raw.ToString();
        }

        private static string GetSqlType(FieldInfo field)
        {
            if (Attribute.IsDefined(field, typeof(MappedString)))
                return "VARCHAR";
            if (field.FieldType == typeof(float))
                return "REAL";
            return "INTEGER";
        }

        private static string GetTableName(Type objType)
        {
            var name = objType.Name;
            return name.EndsWith("_obj") ? name.Substring(0, name.Length - 4) : name;
        }

        private static Type GetMapType(Type objType)
        {
            return Assembly.GetExecutingAssembly().GetType(objType.FullName + "_map");
        }

        private static IEnumerable<Type> GetTypedSectionTypes()
        {
            return Assembly.GetExecutingAssembly()
                           .GetTypes()
                           .Where(t => t.IsSubclassOf(typeof(CatalogueObject)) &&
                                       t.GetCustomAttribute<Section>() != null);
        }

    }
}
