using System.Text.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace BridgeCrew.Launcher;

internal static class ServiceSessionStore
{
    private static string FilePath(string? root)=>Path.Combine(root??AppPaths.Root,"service-session.bin");
    private sealed record SavedSession(string Service,string Token);
    public static async Task Save(Uri service,string token,string? root=null)
    {
        var json=JsonSerializer.Serialize(new SavedSession(service.AbsoluteUri,token));
        var source=CryptographicBuffer.ConvertStringToBinary(json,BinaryStringEncoding.Utf8);
        var protectedData=await new DataProtectionProvider("LOCAL=user").ProtectAsync(source);
        CryptographicBuffer.CopyToByteArray(protectedData,out var bytes);Directory.CreateDirectory(root??AppPaths.Root);
        var path=FilePath(root);var temporary=path+".tmp";await File.WriteAllBytesAsync(temporary,bytes);File.Move(temporary,path,true);
    }
    public static async Task<string?> Load(Uri service,string? root=null)
    {
        var path=FilePath(root);if(!File.Exists(path))return null;
        var bytes=await File.ReadAllBytesAsync(path);
        var source=CryptographicBuffer.CreateFromByteArray(bytes);
        var decrypted=await new DataProtectionProvider().UnprotectAsync(source);
        var saved=JsonSerializer.Deserialize<SavedSession>(CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8,decrypted));
        return saved?.Service==service.AbsoluteUri?saved.Token:null;
    }
    public static void Clear(){var path=FilePath(null);if(File.Exists(path))File.Delete(path);}
}
