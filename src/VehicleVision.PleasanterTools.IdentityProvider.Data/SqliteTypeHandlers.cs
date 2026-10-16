using System.Data;
using System.Globalization;
using Dapper;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>SQLite が文字列・整数で返す値を、Dapper のモデル型へ戻す。</summary>
internal static class SqliteTypeHandlers
{
    private static readonly object Sync = new();
    private static bool _registered;

    public static void Register()
    {
        lock (Sync)
        {
            if (_registered)
            {
                return;
            }

            SqlMapper.AddTypeHandler(new GuidHandler());
            SqlMapper.AddTypeHandler(new DateTimeHandler());
            SqlMapper.AddTypeHandler(new BooleanHandler());

            foreach (var enumType in typeof(DatabaseProvider).Assembly
                .GetTypes()
                .Where(type => type.IsEnum))
            {
                var handlerType = typeof(EnumHandler<>).MakeGenericType(enumType);
                SqlMapper.AddTypeHandler(
                    enumType,
                    (SqlMapper.ITypeHandler)Activator.CreateInstance(handlerType)!);
            }

            foreach (var type in typeof(DatabaseProvider).Assembly
                .GetTypes()
                .Where(HasNamedConstructor))
            {
                SqlMapper.SetTypeMap(type, new ConvertingConstructorTypeMap(type));
            }

            _registered = true;
        }
    }

    private static bool HasNamedConstructor(Type type)
    {
        var propertyNames = type.GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return type.GetConstructors().Any(constructor =>
            constructor.GetParameters() is { Length: > 0 } parameters
            && parameters.All(parameter =>
                parameter.Name is not null && propertyNames.Contains(parameter.Name)));
    }

    private sealed class ConvertingConstructorTypeMap(Type type) : SqlMapper.ITypeMap
    {
        private readonly DefaultTypeMap _default = new(type);

        public System.Reflection.ConstructorInfo? FindConstructor(
            string[] names,
            Type[] types) =>
            _default.FindConstructor(names, types)
            ?? type.GetConstructors().FirstOrDefault(constructor =>
            {
                var parameters = constructor.GetParameters();
                return parameters.Length == names.Length
                    && parameters.Select(parameter => parameter.Name)
                        .SequenceEqual(names, StringComparer.OrdinalIgnoreCase);
            });

        public System.Reflection.ConstructorInfo? FindExplicitConstructor() =>
            _default.FindExplicitConstructor();

        public SqlMapper.IMemberMap? GetConstructorParameter(
            System.Reflection.ConstructorInfo constructor,
            string columnName) =>
            _default.GetConstructorParameter(constructor, columnName);

        public SqlMapper.IMemberMap? GetMember(string columnName) =>
            _default.GetMember(columnName);
    }

    private sealed class GuidHandler : SqlMapper.TypeHandler<Guid>
    {
        public override Guid Parse(object value) => value switch
        {
            Guid guid => guid,
            byte[] bytes => new Guid(bytes),
            _ => Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
        };

        public override void SetValue(IDbDataParameter parameter, Guid value) =>
            parameter.Value = value;
    }

    private sealed class DateTimeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override DateTime Parse(object value) => value switch
        {
            DateTime dateTime => dateTime,
            _ => DateTime.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
        };

        public override void SetValue(IDbDataParameter parameter, DateTime value) =>
            parameter.Value = value;
    }

    private sealed class BooleanHandler : SqlMapper.TypeHandler<bool>
    {
        public override bool Parse(object value) =>
            Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;

        public override void SetValue(IDbDataParameter parameter, bool value) =>
            parameter.Value = value;
    }

    private sealed class EnumHandler<TEnum> : SqlMapper.TypeHandler<TEnum>
        where TEnum : struct, Enum
    {
        public override TEnum Parse(object value) =>
            (TEnum)Enum.ToObject(
                typeof(TEnum),
                Convert.ToInt64(value, CultureInfo.InvariantCulture));

        public override void SetValue(IDbDataParameter parameter, TEnum value) =>
            parameter.Value = Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
