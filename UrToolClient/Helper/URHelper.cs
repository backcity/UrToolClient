using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace UrToolClient.Helper
{
    public static class URHelper
    {

        // 文件解析成 XDocument，供后续读取变量使用
        public static async Task<XDocument> OpenSetUpConfigAsync(string filePath)
        {
            using var fs = File.OpenRead(filePath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz);

            var xml = await sr.ReadToEndAsync();

            return XDocument.Parse(xml);
        }

        /// <summary>
        /// 将 XML 存为 UR 机械臂文件：强制 GZip 压缩，且绝对不包含 <?xml ...?> 头
        /// </summary>
        public static async Task SaveCompressedXmlWithoutHeaderAsync(XDocument doc, string filePath)
        {
            // 1. 创建底层文件流
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);

            // 2. ⚠️ 核心要求 1：套上 GZip 压缩流！
            using var gz = new GZipStream(fs, CompressionMode.Compress);

            // 3. 强制生成无 BOM 的 UTF-8 编码
            var utf8NoBom = new UTF8Encoding(false);

            // 4. ⚠️ 核心要求 2：精准控制 XML 格式，干掉头部
            var settings = new XmlWriterSettings
            {
                Async = true,
                Indent = true,
                Encoding = utf8NoBom,
                OmitXmlDeclaration = true,  // 👈 绝对不生成 <?xml version... ?> 这个头！
                NewLineChars = "\n"         // 👈 强行使用 Linux 标准换行符，防止 UR 控制器报错
            };

            // 5. 将 XmlWriter 对接到 GZip 压缩流上
            using var xmlWriter = XmlWriter.Create(gz, settings);

            // 6. 执行异步保存
            await doc.SaveAsync(xmlWriter, CancellationToken.None);

            // 7. 确保数据全部压入并刷进磁盘
            await xmlWriter.FlushAsync();
        }

        // 使用22端口下载文件，返回本地路径，使用时请用Exception捕获可能的错误
        public static async Task<string> DownLoadSetupFileFromSFTPAsync(string host, string username, string password, string remoteFilePath, CancellationToken cancellation)
        {
            using (var sftp = new SftpClient(host, 22, username, password))
            {
                await sftp.ConnectAsync(cancellation);

                string localDir = Path.GetDirectoryName(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp"));
                if (!Directory.Exists(localDir))
                {
                    Directory.CreateDirectory(localDir);
                }

                string localFilePath = Path.Combine(localDir, Path.GetFileName(remoteFilePath));
                using (var fileStream = File.OpenWrite(Path.Combine(localDir, localFilePath)))
                {
                    await sftp.DownloadFileAsync(remoteFilePath, fileStream, cancellation);
                }

                sftp.Disconnect();

                return localFilePath;
            }
        }


        // 使用22端口上传文件，返回是否成功 不会创建远程目录，使用时请用Exception捕获可能的错误
        public static async Task<bool> UploadSetupFileToSFTPAsync(string host, string username, string password, string localFilePath, string remoteFilePath, CancellationToken cancellation)
        {
            using (var sftp = new SftpClient(host, 22, username, password))
            {
                await sftp.ConnectAsync(cancellation);

                using (var fileStream = File.OpenRead(localFilePath))
                {
                    await sftp.UploadFileAsync(fileStream, remoteFilePath, cancellation);
                }

                sftp.Disconnect();

                return true;
            }
        }

        /// <summary>
        /// 请使用此方法执行远程命令，UR 机械臂的某些操作只能通过命令行完成（比如重启服务），而且 UR 官方也没有提供更友好的接口了，所以只能用这个方法了。使用时请用Exception捕获可能的错误
        /// </summary>
        /// <param name="host"></param>
        /// <param name="username"></param>
        /// <param name="password"></param>
        /// <param name="command"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public static async Task<string> ExecuteCommandAsync(string host, string username, string password, string command, CancellationToken cancellation)
        {
            using (var ssh = new SshClient(host, 22, username, password))
            {
                await ssh.ConnectAsync(cancellation);

                using (var cmd = ssh.CreateCommand(command))
                {
                    string result = await Task.Factory.FromAsync(cmd.BeginExecute, cmd.EndExecute, null);
                    return result;
                }
            }
        }
        //public static Task<string> DownLoadSetupFileFrom

    }
}
