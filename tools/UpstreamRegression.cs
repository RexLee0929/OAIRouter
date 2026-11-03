using OAIRouter;

var secret = "test-local-key-密钥-123";
var encrypted = SecretProtection.Protect(secret);
if (encrypted.Contains(secret, StringComparison.Ordinal) || encrypted == secret) throw new Exception("密钥未加密");
if (SecretProtection.Unprotect(encrypted) != secret) throw new Exception("密钥解密不一致");
if (SecretProtection.Protect("") != "" || SecretProtection.Unprotect("") != "") throw new Exception("空密钥处理失败");
try { SecretProtection.Unprotect("invalid!"); throw new Exception("损坏的密文被接受"); }
catch (FormatException) { }
Console.WriteLine("上游凭据加密回归测试通过");
