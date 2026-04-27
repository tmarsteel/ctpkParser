using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

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
            WriteSchemaSQL(writer);
            WriteDataSQL(lib, writer);
        }

        private static void WriteSchemaSQL(TextWriter writer)
        {
            foreach (var objType in GetTypedSectionTypes().OrderBy(GetTableName))
            {
                var mapType = GetMapType(objType);
                if (mapType == null) continue;

                var fields = mapType.GetFields(BindingFlags.Public | BindingFlags.Instance);

                writer.WriteLine($"CREATE TABLE IF NOT EXISTS [{GetTableName(objType)}] (");
                writer.Write("  [id] INTEGER PRIMARY KEY");

                foreach (var field in fields)
                {
                    writer.WriteLine(",");
                    writer.Write($"  [{field.Name}] {GetSqlType(field)}");
                }

                writer.WriteLine();
                writer.WriteLine(");");
                writer.WriteLine();
            }
        }

        private static void WriteDataSQL(CTPKLib lib, TextWriter writer)
        {
            var sectionTypeMap = GetTypedSectionTypes()
                .ToDictionary(t => t.GetCustomAttribute<Section>().Id);

            foreach (var kvp in lib.Objects.ObjectMap)
            {
                if (!sectionTypeMap.TryGetValue(kvp.Key, out var objType)) continue;

                var mapType = GetMapType(objType);
                if (mapType == null) continue;

                var fields = mapType.GetFields(BindingFlags.Public | BindingFlags.Instance);
                if (fields.Length == 0) continue;

                string tableName = GetTableName(objType);
                string colList = "[id], " + string.Join(", ", fields.Select(f => $"[{f.Name}]"));

                foreach (var obj in kvp.Value)
                {
                    if (obj.Map.GetType() == typeof(ObjMap)) continue;

                    var vals = new List<string> { obj.Id.ToString() };
                    foreach (var field in fields)
                        vals.Add(GetSqlValue(lib, obj, field));

                    writer.WriteLine($"INSERT INTO [{tableName}] ({colList}) VALUES ({string.Join(", ", vals)});");
                }
            }
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
