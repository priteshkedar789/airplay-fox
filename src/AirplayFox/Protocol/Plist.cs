using Claunia.PropertyList;

namespace AirplayFox.Protocol;

/// <summary>Binary plist encode/decode helpers built on plist-cil, converting
/// to/from plain .NET Dictionary/List/primitive trees for convenience.</summary>
public static class Plist
{
    public static byte[] Encode(IDictionary<string, object> dict) =>
        BinaryPropertyListWriter.WriteToArray(ToNs(dict));

    public static Dictionary<string, object> Decode(byte[] data)
    {
        var parsed = PropertyListParser.Parse(data);
        return (Dictionary<string, object>)FromNs(parsed);
    }

    private static NSObject ToNs(object? value) => value switch
    {
        null => new NSDictionary(),
        NSObject ns => ns,
        string s => new NSString(s),
        bool b => new NSNumber(b),
        int i => new NSNumber(i),
        long l => new NSNumber(l),
        double d => new NSNumber(d),
        byte[] b => new NSData(b),
        IDictionary<string, object> dict => DictToNs(dict),
        System.Collections.IEnumerable list => ListToNs(list),
        _ => throw new NotSupportedException($"Cannot encode plist value of type {value.GetType()}"),
    };

    private static NSDictionary DictToNs(IDictionary<string, object> dict)
    {
        var ns = new NSDictionary();
        foreach (var (key, value) in dict) ns.Add(key, ToNs(value));
        return ns;
    }

    private static NSArray ListToNs(System.Collections.IEnumerable list)
    {
        var items = list.Cast<object>().Select(ToNs).ToArray();
        return new NSArray(items);
    }

    private static object FromNs(NSObject obj) => obj switch
    {
        NSDictionary d => d.ToDictionary(kv => kv.Key, kv => FromNs(kv.Value)),
        NSArray a => a.Select(FromNs).ToList(),
        NSString s => s.Content,
        NSNumber n => n.ToObject(),
        NSData data => data.Bytes,
        _ => throw new NotSupportedException($"Cannot decode plist value of type {obj.GetType()}"),
    };
}
