using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Inkpact.Data;

/// <summary>Gives columns, keys, indexes and foreign keys snake_case names, as the architecture's table list uses.</summary>
internal static class SnakeCaseNames
{
    public static void UseSnakeCaseNames(this ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(key.IsPrimaryKey()
                    ? $"pk_{table}"
                    : $"ak_{table}_{Join(key.Properties)}");
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName($"fk_{table}_{Join(foreignKey.Properties)}");
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName($"ix_{table}_{Join(index.Properties)}");
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string Join(IEnumerable<IReadOnlyProperty> properties) =>
        string.Join('_', properties.Select(p => ToSnakeCase(p.Name)));
}
