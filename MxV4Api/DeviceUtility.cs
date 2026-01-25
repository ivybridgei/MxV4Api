using System.Text.RegularExpressions;

namespace MxV4Api.Services
{
    public static class DeviceUtility
    {
        // 1. 定义位软元件集合 (需要特殊处理解压)
        private static readonly HashSet<string> BitDevices = new(StringComparer.OrdinalIgnoreCase)
        {
            "X", "Y", "M", "L", "B", "F", "S"
        };

        // 2. 定义使用 16 进制地址的软元件 (X, Y, B, W 等通常是 Hex)
        private static readonly HashSet<string> HexAddrDevices = new(StringComparer.OrdinalIgnoreCase)
        {
            "X", "Y", "B", "W", "SB", "SW"
        };

        /// <summary>
        /// 判断是否为位软元件
        /// </summary>
        public static bool IsBitDevice(string deviceHead)
        {
            return BitDevices.Contains(deviceHead);
        }

        /// <summary>
        /// 解析软元件字符串
        /// 例如: "M100" -> Head="M", Address=100, IsHex=false
        /// 例如: "X1F"  -> Head="X", Address=31,  IsHex=true
        /// </summary>
        public static (string Head, int Address, bool IsHex) ParseDevice(string deviceStr)
        {
            if (string.IsNullOrWhiteSpace(deviceStr))
                throw new ArgumentException("Device address cannot be empty");

            // 使用正则分离字母和数字
            var match = Regex.Match(deviceStr.Trim(), @"^([A-Z]+)([0-9A-F]+)$", RegexOptions.IgnoreCase);
            if (!match.Success)
                throw new ArgumentException($"Invalid device format: {deviceStr}");

            string head = match.Groups[1].Value.ToUpper();
            string numberStr = match.Groups[2].Value;
            bool isHex = HexAddrDevices.Contains(head);

            int address;
            try
            {
                // 根据软元件类型决定是按 10 进制还是 16 进制解析数值
                address = Convert.ToInt32(numberStr, isHex ? 16 : 10);
            }
            catch
            {
                throw new ArgumentException($"Invalid address number: {numberStr} for device {head}");
            }

            return (head, address, isHex);
        }

        /// <summary>
        /// 重新组合地址字符串
        /// </summary>
        public static string BuildDeviceStr(string head, int address, bool isHex)
        {
            return head + (isHex ? address.ToString("X") : address.ToString());
        }
    }
}