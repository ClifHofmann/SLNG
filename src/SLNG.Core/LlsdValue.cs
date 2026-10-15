using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SLNG.Core;

public sealed class LlsdValue
{
    public static readonly LlsdValue Undefined = new(LlsdKind.Undefined);

    private readonly bool _boolVal;
    private readonly int _intVal;
    private readonly double _realVal;
    private readonly string? _strVal;
    private readonly Guid _uuidVal;
    private readonly IReadOnlyDictionary<string, LlsdValue>? _map;
    private readonly IReadOnlyList<LlsdValue>? _array;

    public LlsdKind Kind { get; }

    public bool IsDefined => Kind != LlsdKind.Undefined;
    public bool IsBoolean => Kind == LlsdKind.Boolean;
    public bool IsInteger => Kind == LlsdKind.Integer; // strictly Integer: 'i1', not 'r1', not '1'
    public bool IsReal => Kind == LlsdKind.Real;       // strictly Real
    public bool IsUuid => Kind == LlsdKind.Uuid;
    public bool IsMap => Kind == LlsdKind.Map;
    public bool IsArray => Kind == LlsdKind.Array;

    public int Count => _map?.Count ?? _array?.Count ?? 0;

    public LlsdValue this[string key] => _map != null && _map.TryGetValue(key, out var v) ? v : Undefined;

    public LlsdValue this[int index] => _array != null && index >= 0 && index < _array.Count ? _array[index] : Undefined;

    public IEnumerable<KeyValuePair<string, LlsdValue>> Entries => _map ?? Enumerable.Empty<KeyValuePair<string, LlsdValue>>();

    public LlsdValue(LlsdKind kind)
    {
        Kind = kind;
    }

    private LlsdValue(bool value)
    {
        Kind = LlsdKind.Boolean;
        _boolVal = value;
    }

    private LlsdValue(int value)
    {
        Kind = LlsdKind.Integer;
        _intVal = value;
    }

    private LlsdValue(double value)
    {
        Kind = LlsdKind.Real;
        _realVal = value;
    }

    private LlsdValue(string value)
    {
        Kind = LlsdKind.String;
        _strVal = value;
    }

    private LlsdValue(Guid value)
    {
        Kind = LlsdKind.Uuid;
        _uuidVal = value;
    }

    private LlsdValue(IReadOnlyDictionary<string, LlsdValue> map)
    {
        Kind = LlsdKind.Map;
        _map = map;
    }

    private LlsdValue(IReadOnlyList<LlsdValue> array)
    {
        Kind = LlsdKind.Array;
        _array = array;
    }

    public static LlsdValue FromBoolean(bool value) => new(value);
    public static LlsdValue FromInteger(int value) => new(value);
    public static LlsdValue FromReal(double value) => new(value);
    public static LlsdValue FromString(string value) => new(value);
    public static LlsdValue FromUuid(Guid value) => new(value);
    public static LlsdValue FromMap(IReadOnlyDictionary<string, LlsdValue> map) => new(map);
    public static LlsdValue FromArray(IReadOnlyList<LlsdValue> array) => new(array);
    public static LlsdValue Other() => new(LlsdKind.Other);

    public bool AsBoolean()
    {
        return Kind switch
        {
            LlsdKind.Boolean => _boolVal,
            LlsdKind.Integer => _intVal != 0,
            LlsdKind.Real => _realVal != 0.0 && !double.IsNaN(_realVal),
            LlsdKind.String => !string.IsNullOrEmpty(_strVal) && _strVal != "0" && !string.Equals(_strVal, "false", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public int AsInteger()
    {
        return Kind switch
        {
            LlsdKind.Integer => _intVal,
            LlsdKind.Real => double.IsNaN(_realVal) || double.IsInfinity(_realVal) ? 0 : (int)_realVal,
            LlsdKind.Boolean => _boolVal ? 1 : 0,
            LlsdKind.String => int.TryParse(_strVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0,
            _ => 0
        };
    }

    public double AsReal()
    {
        return Kind switch
        {
            LlsdKind.Real => _realVal,
            LlsdKind.Integer => _intVal,
            LlsdKind.Boolean => _boolVal ? 1.0 : 0.0,
            LlsdKind.String => double.TryParse(_strVal, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0,
            _ => 0.0
        };
    }

    public Guid AsUuid()
    {
        return Kind == LlsdKind.Uuid ? _uuidVal : Guid.Empty;
    }

    public string AsString()
    {
        return Kind switch
        {
            LlsdKind.String => _strVal ?? "",
            LlsdKind.Uuid => _uuidVal.ToString(),
            LlsdKind.Integer => _intVal.ToString(CultureInfo.InvariantCulture),
            LlsdKind.Real => _realVal.ToString(CultureInfo.InvariantCulture),
            LlsdKind.Boolean => _boolVal ? "true" : "false",
            _ => ""
        };
    }
}
