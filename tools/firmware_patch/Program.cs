using System.Globalization;
using System.Text;

// Patches the Myo firmware 1.5.1970 S-record image and recomputes the checksum records the
// bootloader verifies before starting the application. Three changes:
//
//   - the built-in classifier history index bug (0x1848A SUBS R4,R6,R4 -> ADDS R4,R6,R4);
//   - the classifier no longer runs until a BLE central asks for it, instead of from boot and again
//     after every disconnect;
//   - the version becomes 1.5.1972.

if (args.Length != 2)
{
  Console.WriteLine("Usage: MyoFirmwarePatch <stock-1.5.1970.hex> <output.hex>");
  return 2;
}
string Input = Path.GetFullPath(args[0]);
string Output = Path.GetFullPath(args[1]);
if (string.Equals(Input, Output, StringComparison.OrdinalIgnoreCase))
{
  Console.Error.WriteLine("Input and output paths must differ.");
  return 2;
}
// (address, original bytes, new bytes)
var patches = new List<(uint Address, byte[] Original, byte[] New)>
{
  // History gather: SUBS R4,R6,R4 -> ADDS R4,R6,R4.
  (0x1848A, new byte[] { 0x34, 0x1B }, new byte[] { 0x34, 0x19 }),
  // fw_main: drop the boot-time BL fw_classifier_enable (0x13100), two 16-bit NOPs. The flag stays
  // 0, EMG stays stopped and the IMU stays in the low-power state the IMU init left it in, so a
  // worn armband runs no sync machine and reports no poses until a central enables the classifier.
  (0x15CBE, new byte[] { 0xFD, 0xF7, 0x1F, 0xFA }, new byte[] { 0x00, 0xBF, 0x00, 0xBF }),
  // BLE disconnect handler: BL fw_classifier_enable (0x13100) -> BL fw_classifier_disable (0x1323C).
  // Not a NOP, because the host may have enabled the classifier before dropping the link and the
  // flag would stay set. fw_classifier_disable clears the flag before fw_on_arm_unsynced, so the
  // sync-state setter sees the classifier disabled and does not buzz.
  (0x1338A, new byte[] { 0xFF, 0xF7, 0xB9, 0xFE }, new byte[] { 0xFF, 0xF7, 0x57, 0xFF }),
  // Version header (major, minor, patch, hwrev u16 at 0x10188): patch 1970 -> 1972 (BLE fw_version).
  (0x1018C, new byte[] { 0xB2, 0x07 }, new byte[] { 0xB4, 0x07 }),
  // INFO.TXT JSON template: "patch": 1970 -> 1972.
  (0x10246, Encoding.ASCII.GetBytes("1970"), Encoding.ASCII.GetBytes("1972")),
};

var lines = File.ReadAllLines(Input);
var image = new byte[0x100000];
var present = new bool[0x100000];
Array.Fill(image, (byte)0xFF);
foreach (var line in lines)
{
  if (TryParseData(line, out uint address, out byte[] data))
  {
    for (int i = 0; i < data.Length; i++)
    {
      image[address + i] = data[i];
      present[address + i] = true;
    }
  }
}

Console.WriteLine("Original image:");
if (!BootloaderAccepts(image, true))
{
  Console.WriteLine("Original image fails the emulated bootloader check, aborting.");
  return 1;
}

var patched = (byte[])image.Clone();
foreach (var patch in patches)
{
  if (!image.AsSpan((int)patch.Address, patch.Original.Length).SequenceEqual(patch.Original))
  {
    Console.WriteLine($"Unexpected bytes at 0x{patch.Address:X}, aborting.");
    return 1;
  }
  patch.New.CopyTo(patched, patch.Address);
}
UpdateChecksums(patched);

Console.WriteLine("Patched image:");
if (!BootloaderAccepts(patched, true))
{
  Console.WriteLine("Patched image fails the emulated bootloader check, aborting.");
  return 1;
}

var changed = new List<uint>();
for (uint a = 0; a < image.Length; a++)
{
  if (image[a] != patched[a])
  {
    changed.Add(a);
    if (!present[a])
    {
      Console.WriteLine($"Changed byte 0x{a:X} is not covered by any S-record, aborting.");
      return 1;
    }
  }
}
Console.WriteLine($"Changed bytes: {string.Join(" ", changed.Select(a => $"0x{a:X}:{image[a]:X2}->{patched[a]:X2}"))}");

var output = new StringBuilder();
int rewritten = 0;
foreach (var line in lines)
{
  if (TryParseData(line, out uint address, out byte[] data))
  {
    var newData = new byte[data.Length];
    Array.Copy(patched, address, newData, 0, data.Length);
    string newLine = FormatData(line[1], address, newData);
    if (newLine != line)
    {
      rewritten++;
    }
    output.Append(newLine).Append("\r\n");
  }
  else
  {
    output.Append(line).Append("\r\n");
  }
}
File.WriteAllText(Output, output.ToString(), Encoding.ASCII);
Console.WriteLine($"Rewrote {rewritten} S-record lines, wrote {Output}");

// Round trip: re-read the output and check it again.
var check = new byte[0x100000];
Array.Fill(check, (byte)0xFF);
foreach (var line in File.ReadAllLines(Output))
{
  if (TryParseData(line, out uint address, out byte[] data))
  {
    Array.Copy(data, 0, check, address, data.Length);
  }
}
bool same = check.AsSpan().SequenceEqual(patched);
Console.WriteLine($"Round trip identical: {same}");
return same ? 0 : 1;

static bool TryParseData(string line, out uint address, out byte[] data)
{
  address = 0;
  data = Array.Empty<byte>();
  if (line.Length < 4 || line[0] != 'S' || (line[1] != '1' && line[1] != '2' && line[1] != '3'))
  {
    return false;
  }
  int addressBytes = line[1] - '0' + 1;
  byte[] raw = Convert.FromHexString(line.AsSpan(2));
  int count = raw[0];
  if (count != raw.Length - 1)
  {
    throw new InvalidDataException($"Bad record length: {line}");
  }
  int sum = 0;
  for (int i = 0; i < raw.Length - 1; i++)
  {
    sum += raw[i];
  }
  if ((byte)~sum != raw[^1])
  {
    throw new InvalidDataException($"Bad record checksum: {line}");
  }
  for (int i = 0; i < addressBytes; i++)
  {
    address = (address << 8) | raw[1 + i];
  }
  data = raw.AsSpan(1 + addressBytes, count - addressBytes - 1).ToArray();
  return true;
}

static string FormatData(char type, uint address, byte[] data)
{
  int addressBytes = type - '0' + 1;
  var raw = new List<byte> { (byte)(addressBytes + data.Length + 1) };
  for (int i = addressBytes - 1; i >= 0; i--)
  {
    raw.Add((byte)(address >> (8 * i)));
  }
  raw.AddRange(data);
  int sum = 0;
  foreach (var b in raw)
  {
    sum += b;
  }
  raw.Add((byte)~sum);
  return "S" + type + Convert.ToHexString(raw.ToArray());
}

// Bootloader sub_3BB4.
static uint Fletcher32(byte[] image, uint offset, uint size)
{
  uint sum2 = 0xFFFF;
  uint sum1 = 0xFFFF;
  for (uint i = 0; i < size; i++)
  {
    if (sum2 >= 0x80000000)
    {
      sum2 = (sum2 & 0xFFFF) + (sum2 >> 16);
      sum1 = (sum1 & 0xFFFF) + (sum1 >> 16);
    }
    sum1 += image[offset + i];
    sum2 += sum1;
  }
  while (sum2 > 0xFFFF)
  {
    sum2 = (sum2 & 0xFFFF) + (sum2 >> 16);
  }
  while (sum1 > 0xFFFF)
  {
    sum1 = (sum1 & 0xFFFF) + (sum1 >> 16);
  }
  return sum1 | (sum2 << 16);
}

static uint U32(byte[] image, uint address)
{
  return BitConverter.ToUInt32(image, (int)address);
}

static ushort U16(byte[] image, uint address)
{
  return BitConverter.ToUInt16(image, (int)address);
}

// Bootloader sub_4970 + sub_3C08 for the application at 0x10000.
static bool BootloaderAccepts(byte[] image, bool verbose)
{
  const uint App = 0x10000;
  uint sp = U32(image, App);
  uint reset = U32(image, App + 4);
  if (sp == 0xFFFFFFFF || reset == 0xFFFFFFFF || U32(image, reset & ~1u) == 0xFFFFFFFF)
  {
    Console.WriteLine("  vector table blank");
    return false;
  }
  ushort hardwareRevision = U16(image, App + 398);
  if (hardwareRevision != 0xFFFF && hardwareRevision != 2)
  {
    Console.WriteLine($"  hardware revision {hardwareRevision} rejected");
    return false;
  }
  uint table = U32(image, App + 508);
  if (table == 0xFFFFFFFF)
  {
    Console.WriteLine("  no checksum table (accepted without verification)");
    return true;
  }
  uint record = table;
  while (true)
  {
    ushort type = U16(image, record);
    ushort length = U16(image, record + 2);
    if (type == 0)
    {
      return true;
    }
    if (type == 0xFFFF)
    {
      Console.WriteLine($"  record list hits erased flash at 0x{record:X}");
      return false;
    }
    if (type == 1)
    {
      uint offset = U32(image, record + 4);
      uint size = U32(image, record + 8);
      uint stored = U32(image, record + 12);
      uint computed = Fletcher32(image, offset, size);
      if (verbose)
      {
        Console.WriteLine($"  record at 0x{record:X}: range 0x{offset:X}+0x{size:X} stored 0x{stored:X8} computed 0x{computed:X8} {(stored == computed ? "OK" : "MISMATCH")}");
      }
      if (stored != computed)
      {
        return false;
      }
    }
    record += length;
  }
}

static void UpdateChecksums(byte[] image)
{
  uint table = U32(image, 0x10000 + 508);
  uint record = table;
  while (U16(image, record) != 0 && U16(image, record) != 0xFFFF)
  {
    if (U16(image, record) == 1)
    {
      uint offset = U32(image, record + 4);
      uint size = U32(image, record + 8);
      if (record + 16 > offset && record < offset + size)
      {
        throw new InvalidOperationException("Checksum record lies inside its own range");
      }
      BitConverter.GetBytes(Fletcher32(image, offset, size)).CopyTo(image, record + 12);
    }
    record += U16(image, record + 2);
  }
}
