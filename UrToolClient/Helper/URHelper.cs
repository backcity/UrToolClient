using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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

        //public static Task<string> DownLoadSetupFileFrom

    }
}
