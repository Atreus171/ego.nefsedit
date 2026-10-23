using System.IO.Abstractions;
using System.Text;
using VictorBush.Ego.NefsLib;
using VictorBush.Ego.NefsLib.DataSource;
using VictorBush.Ego.NefsLib.IO;
using VictorBush.Ego.NefsLib.Item;
using VictorBush.Ego.NefsLib.Progress;

namespace NefsTool;

internal static class Program
{
	private static async Task<int> Main(string[] args)
	{
		if (args.Length == 0)
		{
			PrintUsage();
			return 2;
		}

		return args[0].ToLowerInvariant() switch
		{
			"info" when args.Length >= 2 => await Info(args[1]),
			"save" when args.Length >= 3 => await Save(args[1], args[2]),
			"cmp" when args.Length >= 3 => await Compare(args[1], args[2]),
			"list" when args.Length >= 2 => await List(args[1], args.Length >= 3 ? args[2] : null),
			"grep" when args.Length >= 3 => await Search(args[1], args[2], false, args.Length >= 4 ? args[3] : null),
			"grepci" when args.Length >= 3 => await Search(args[1], args[2], true, args.Length >= 4 ? args[3] : null),
			"extract" when args.Length >= 4 => await Extract(args[1], args[2], args[3]),
			"extract-all" when args.Length >= 3 => await ExtractAll(args[1], args[2], args.Length >= 4 ? args[3] : null),
			"replace" when args.Length >= 5 && args.Length % 2 == 1 => await Replace(args[1], args[2], args[3..]),
			"add" when args.Length >= 5 && (args.Length - 3) % 2 == 0 => await Add(args[1], args[2], args[3..]),
			_ => PrintUsageAndFail(),
		};
	}

	private static async Task<int> List(string archivePath, string? filter)
	{
		var archive = await LoadArchive(archivePath);
		foreach (var item in archive.Items.EnumerateById())
		{
			if (item.Type != NefsItemType.File)
			{
				continue;
			}

			var path = archive.Items.GetItemFilePath(item.Id).Replace('\\', '/');
			if (!string.IsNullOrEmpty(filter) &&
			    !path.Contains(filter, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var t = item.Transform;
			var transform = t is null
				? "none"
				: $"{(t.IsZlibCompressed ? "zlib " : "")}{(t.IsAesEncrypted ? "aes " : "")}{(t.IsLzssCompressed ? "lzss" : "")}".Trim();
			Console.WriteLine(
				$"id={item.Id.Value,5} vol={item.Attributes.Volume} ext={item.ExtractedSize,10} comp={item.CompressedSize,10} " +
				$"chunks={item.DataSource.Size.Chunks.Count,4} xform={transform,-9} '{path}'");
		}

		return 0;
	}

	private static async Task<int> Replace(string archivePath, string outPath, string[] nameFilePairs)
	{
		var archive = await LoadArchive(archivePath);
		var fs = new FileSystem();

		for (var i = 0; i < nameFilePairs.Length; i += 2)
		{
			var selector = nameFilePairs[i];
			var replacementPath = Path.GetFullPath(nameFilePairs[i + 1]);
			if (!File.Exists(replacementPath))
			{
				Console.WriteLine($"replacement file not found: {replacementPath}");
				return 1;
			}

			var matches = ResolveItems(archive, selector);
			if (matches.Count != 1)
			{
				PrintMatchError("replace", selector, matches);
				return 1;
			}

			var item = matches[0];
			var oldSize = item.ExtractedSize;
			var newSize = (uint)new FileInfo(replacementPath).Length;
			var dataSource = new NefsFileDataSource(replacementPath, 0, new NefsItemSize(newSize), false);
			foreach (var duplicate in archive.Items.GetItemDuplicates(item.Id))
			{
				duplicate.UpdateDataSource(dataSource, NefsItemState.Replaced);
			}

			Console.WriteLine(
				$"REPLACE id={item.Id.Value} '{archive.Items.GetItemFilePath(item.Id)}' vol={item.Attributes.Volume} " +
				$"size={oldSize} -> {newSize} duplicates={archive.Items.GetItemDuplicates(item.Id).Count}");
		}

		var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
		Directory.CreateDirectory(outDir);
		var tempDir = Path.Combine(outDir, ".nefstool-tmp");
		var writer = new NefsWriter(tempDir, fs, new NefsTransformer(fs));

		Console.WriteLine($"SAVING -> {outPath}");
		var before = DateTime.Now;
		var (newArchive, _) = await writer.WriteArchiveAsync(outPath, archive, new NefsProgress());
		Console.WriteLine($"SAVE OK in {(DateTime.Now - before).TotalSeconds:F1}s");

		var transformer = new NefsTransformer(fs);
		foreach (var i in Enumerable.Range(0, nameFilePairs.Length / 2))
		{
			var selector = nameFilePairs[i * 2];
			var replacementPath = Path.GetFullPath(nameFilePairs[i * 2 + 1]);
			var item = ResolveItems(newArchive, selector).SingleOrDefault();
			if (item is null)
			{
				Console.WriteLine($"VERIFY '{selector}': MISSING");
				continue;
			}

			var expected = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(replacementPath));
			var actual = await HashItemAsync(fs, transformer, item);
			var state = expected.AsSpan().SequenceEqual(actual) ? "OK" : "MISMATCH";
			Console.WriteLine(
				$"VERIFY '{newArchive.Items.GetItemFilePath(item.Id)}': {state} id={item.Id.Value} " +
				$"vol={item.Attributes.Volume} ext={item.ExtractedSize} chunks={item.DataSource.Size.Chunks.Count}");
		}

		return 0;
	}

	private static async Task<int> Add(string archivePath, string outPath, string[] nameFilePairs)
	{
		var archive = await LoadArchive(archivePath);
		var fs = new FileSystem();
		var nextId = archive.Items.EnumerateById().Select(x => x.Id.Value).Max() + 1;

		for (var i = 0; i < nameFilePairs.Length; i += 2)
		{
			var name = nameFilePairs[i];
			var filePath = Path.GetFullPath(nameFilePairs[i + 1]);
			if (!File.Exists(filePath))
			{
				Console.WriteLine($"file not found: {filePath}");
				return 1;
			}

			var normalized = NormalizeArchivePath(name);
			var segments = normalized.Split('/');
			if (segments.Length == 0 || segments.Any(x => x.Length == 0 || x == "." || x == ".."))
			{
				Console.WriteLine($"invalid archive path: '{name}'");
				return 1;
			}

			var fileName = segments[^1];
			var id = new NefsItemId(nextId++);
			var directoryPath = segments.Length > 1 ? string.Join('/', segments[..^1]) : null;
			var parent = directoryPath is null ? null : FindDirectory(archive, directoryPath);
			if (directoryPath is not null && parent is null)
			{
				Console.WriteLine($"parent directory not found: '{directoryPath}'");
				return 1;
			}

			var directoryId = parent?.Id ?? id;
			var duplicate = archive.Items.EnumerateById().Any(x =>
				x.Type == NefsItemType.File &&
				string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase) &&
				(directoryPath is null ? x.DirectoryId == x.Id : x.DirectoryId == directoryId));
			if (duplicate)
			{
				Console.WriteLine($"item already exists: '{normalized}'");
				return 1;
			}

			var size = (uint)new FileInfo(filePath).Length;
			var dataSource = new NefsFileDataSource(filePath, 0, new NefsItemSize(size), false);
			var transform = new NefsDataTransform(0x20000, true);
			var attributes = new NefsItemAttributes { Volume = 0, IsTransformed = true };
			var item = new NefsItem(id, id, fileName, directoryId, dataSource, transform, attributes,
				NefsItemState.Added);
			archive.Items.Add(item);
			Console.WriteLine($"ADD id={id.Value} '{normalized}' size={size}");
		}

		var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
		Directory.CreateDirectory(outDir);
		var tempDir = Path.Combine(outDir, ".nefstool-tmp");
		var writer = new NefsWriter(tempDir, fs, new NefsTransformer(fs));

		Console.WriteLine($"SAVING -> {outPath}");
		var before = DateTime.Now;
		var (newArchive, _) = await writer.WriteArchiveAsync(outPath, archive, new NefsProgress());
		Console.WriteLine($"SAVE OK in {(DateTime.Now - before).TotalSeconds:F1}s");

		var transformer = new NefsTransformer(fs);
		for (var i = 0; i < nameFilePairs.Length; i += 2)
		{
			var name = NormalizeArchivePath(nameFilePairs[i]);
			var filePath = Path.GetFullPath(nameFilePairs[i + 1]);
			var item = newArchive.Items.EnumerateById()
				.FirstOrDefault(x => x.Type == NefsItemType.File &&
				                     string.Equals(NormalizeArchivePath(newArchive.Items.GetItemFilePath(x.Id)),
					                     name, StringComparison.OrdinalIgnoreCase));
			if (item is null)
			{
				Console.WriteLine($"VERIFY '{name}': MISSING");
				continue;
			}

			var expected = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(filePath));
			var actual = await HashItemAsync(fs, transformer, item);
			var state = expected.AsSpan().SequenceEqual(actual) ? "OK" : "MISMATCH";
			Console.WriteLine(
				$"VERIFY '{name}': {state} id={item.Id.Value} vol={item.Attributes.Volume} ext={item.ExtractedSize} " +
				$"chunks={item.DataSource.Size.Chunks.Count}");
		}

		return 0;
	}

	private static async Task<int> Extract(string archivePath, string selector, string outPath)
	{
		var archive = await LoadArchive(archivePath);
		var fs = new FileSystem();
		var transformer = new NefsTransformer(fs);
		var matches = ResolveItems(archive, selector);
		if (matches.Count != 1)
		{
			PrintMatchError("extract", selector, matches);
			return 1;
		}

		var item = matches[0];
		await ExtractItemAsync(fs, transformer, item, outPath);
		Console.WriteLine(
			$"EXTRACT '{archive.Items.GetItemFilePath(item.Id)}' -> {outPath} size={item.ExtractedSize}");
		return 0;
	}

	private static async Task<int> ExtractAll(string archivePath, string outDir, string? filter)
	{
		var archive = await LoadArchive(archivePath);
		var fs = new FileSystem();
		var transformer = new NefsTransformer(fs);
		var root = Path.GetFullPath(outDir);
		Directory.CreateDirectory(root);

		var files = archive.Items.EnumerateById()
			.Where(x => x.Type == NefsItemType.File)
			.Select(x => (Item: x, Path: NormalizeArchivePath(archive.Items.GetItemFilePath(x.Id))))
			.Where(x => filter is null || x.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
			.ToList();
		var duplicatePaths = files.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
			.Where(g => g.Count() > 1)
			.Select(g => g.Key)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var count = 0;
		foreach (var (item, path) in files)
		{
			var outPath = SafeOutputPath(root, path);
			if (duplicatePaths.Contains(path))
			{
				var extension = Path.GetExtension(outPath);
				outPath = outPath[..^extension.Length] + $"~dup{item.Id.Value}" + extension;
			}

			await ExtractItemAsync(fs, transformer, item, outPath);
			count++;
		}

		Console.WriteLine($"EXTRACT-ALL {count} file(s) -> {root}");
		return 0;
	}

	private static async Task ExtractItemAsync(IFileSystem fs, NefsTransformer transformer, NefsItem item,
		string outPath)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
		await using var input = item.DataSource.OpenRead(fs);
		await using var output = File.Create(outPath);
		await transformer.DetransformAsync(
			input,
			item.DataSource.Offset,
			output,
			0,
			item.ExtractedSize,
			item.DataSource.Size.Chunks,
			new NefsProgress());
	}

	private static async Task<int> Search(string archivePath, string text, bool ignoreCase, string? maxMbText)
	{
		if (string.IsNullOrEmpty(text))
		{
			Console.WriteLine("search text cannot be empty");
			return 1;
		}

		var maxBytes = 256L * 1024 * 1024;
		if (maxMbText is not null)
		{
			if (!int.TryParse(maxMbText, out var maxMb) || maxMb < 0)
			{
				Console.WriteLine($"invalid max MB value: '{maxMbText}'");
				return 1;
			}

			maxBytes = maxMb == 0 ? long.MaxValue : maxMb * 1024L * 1024L;
		}

		var archive = await LoadArchive(archivePath);
		var fs = new FileSystem();
		var transformer = new NefsTransformer(fs);
		var hits = 0;
		var skipped = 0;

		foreach (var item in archive.Items.EnumerateById())
		{
			if (item.Type != NefsItemType.File)
			{
				continue;
			}

			if (item.ExtractedSize > maxBytes)
			{
				skipped++;
				continue;
			}

			try
			{
				using var input = item.DataSource.OpenRead(fs);
				using var output = new MemoryStream();
				await transformer.DetransformAsync(
					input,
					item.DataSource.Offset,
					output,
					0,
					item.ExtractedSize,
					item.DataSource.Size.Chunks,
					new NefsProgress());
				var data = output.ToArray();
				var itemPath = NormalizeArchivePath(archive.Items.GetItemFilePath(item.Id));

				void Scan(Encoding encoding, int stride, string encodingName)
				{
					var decoded = encoding.GetString(data);
					var offset = 0;
					while (offset <= decoded.Length - text.Length)
					{
						var i = decoded.IndexOf(text, offset,
							ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
						if (i < 0)
						{
							break;
						}

						var byteOffset = stride == 1 && encoding.CodePage == Encoding.UTF8.CodePage
							? Encoding.UTF8.GetByteCount(decoded.AsSpan(0, i))
							: i * stride;
						var start = Math.Max(0, byteOffset - 80);
						var end = Math.Min(data.Length, byteOffset + text.Length * stride + 120);
						var context = Encoding.UTF8.GetString(data, start, end - start)
							.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
						Console.WriteLine(
							$"HIT id={item.Id.Value} '{itemPath}' enc={encodingName} at={byteOffset}");
						Console.WriteLine($"  {context}");
						hits++;
						offset = i + text.Length;
					}
				}

				Scan(Encoding.Latin1, 1, "latin1");
				Scan(Encoding.UTF8, 1, "utf8");
				Scan(Encoding.Unicode, 2, "utf16le");
				Scan(Encoding.BigEndianUnicode, 2, "utf16be");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"SKIP id={item.Id.Value} '{item.FileName}': {ex.GetType().Name}");
			}
		}

		Console.WriteLine($"hits={hits} skipped={skipped}" +
			(maxBytes == long.MaxValue ? "" : $" max={maxBytes / (1024 * 1024)}MB"));
		return 0;
	}
	private static async Task<int> Info(string archivePath)
	{
		var archive = await LoadArchive(archivePath);
		PrintInfo(archivePath, archive);
		return 0;
	}

	private static async Task<int> Save(string archivePath, string outPath)
	{
		var archive = await LoadArchive(archivePath);
		Console.WriteLine($"LOAD OK: {archivePath}");
		PrintInfo(archivePath, archive);

		var fs = new FileSystem();
		var tempDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, ".nefstool-tmp");
		var writer = new NefsWriter(tempDir, fs, new NefsTransformer(fs));

		Console.WriteLine($"SAVING -> {outPath}");
		var before = DateTime.Now;
		var (newArchive, _) = await writer.WriteArchiveAsync(outPath, archive, new NefsProgress());
		Console.WriteLine($"SAVE OK in {(DateTime.Now - before).TotalSeconds:F1}s");

		var outInfo = await LoadArchive(outPath);
		PrintInfo(outPath, outInfo);

		// Cross-check volume membership between source and saved archives.
		var srcVol = archive.Items.EnumerateById().GroupBy(x => x.Attributes.Volume)
			.ToDictionary(g => g.Key, g => g.Count());
		var dstVol = outInfo.Items.EnumerateById().GroupBy(x => x.Attributes.Volume)
			.ToDictionary(g => g.Key, g => g.Count());
		Console.WriteLine("--- volume membership (volume -> item count) SOURCE vs SAVED ---");
		foreach (var key in srcVol.Keys.OrderBy(x => x))
		{
			Console.WriteLine($"  vol {key}: src={srcVol[key]} dst={(dstVol.GetValueOrDefault(key))}");
		}

		// Verify volume-1 items kept their original offsets.
		var srcVol1Offsets = archive.Items.EnumerateById()
			.Where(x => x.Attributes.Volume == 1)
			.ToDictionary(x => x.Id, x => x.DataSource.Offset);
		var bad = 0;
		foreach (var (id, offset) in srcVol1Offsets)
		{
			var dstItem = outInfo.Items.EnumerateById().FirstOrDefault(x => x.Id == id);
			if (dstItem is null || dstItem.DataSource.Offset != offset)
			{
				bad++;
			}
		}

		Console.WriteLine($"volume-1 items kept original offsets: {(bad == 0 ? "YES" : $"NO ({bad} mismatches)")}");
		return 0;
	}

	private static async Task<int> Compare(string aPath, string bPath)
	{
		var a = await LoadArchive(aPath);
		var b = await LoadArchive(bPath);
		var fs = new FileSystem();
		var transformer = new NefsTransformer(fs);
		var bById = b.Items.EnumerateById().ToDictionary(x => x.Id.Value);

		var contentDiff = 0;
		var sizeDiff = 0;
		var chunkDiff = 0;
		var transformDiff = 0;
		var missing = 0;
		var listed = 0;

		foreach (var ai in a.Items.EnumerateById())
		{
			if (ai.Type != NefsItemType.File)
			{
				continue;
			}

			if (!bById.TryGetValue(ai.Id.Value, out var bi))
			{
				missing++;
				continue;
			}

			var aZlib = ai.Transform?.IsZlibCompressed ?? false;
			var bZlib = bi.Transform?.IsZlibCompressed ?? false;
			var aAes = ai.Transform?.IsAesEncrypted ?? false;
			var bAes = bi.Transform?.IsAesEncrypted ?? false;
			var aLzss = ai.Transform?.IsLzssCompressed ?? false;
			var bLzss = bi.Transform?.IsLzssCompressed ?? false;

			if (aZlib != bZlib || aAes != bAes || aLzss != bLzss)
			{
				transformDiff++;
			}

			if (ai.ExtractedSize != bi.ExtractedSize)
			{
				sizeDiff++;
			}

			if (ai.DataSource.Size.Chunks.Count != bi.DataSource.Size.Chunks.Count)
			{
				chunkDiff++;
			}

			var ha = await HashItemAsync(fs, transformer, ai);
			var hb = await HashItemAsync(fs, transformer, bi);
			if (!ha.AsSpan().SequenceEqual(hb))
			{
				contentDiff++;
				if (listed < 20)
				{
					listed++;
					Console.WriteLine($"CONTENT DIFF id={ai.Id.Value} '{ai.FileName}' " +
						$"ext={ai.ExtractedSize}/{bi.ExtractedSize} chunks={ai.DataSource.Size.Chunks.Count}/{bi.DataSource.Size.Chunks.Count} " +
						$"zlib={aZlib}/{bZlib} aes={aAes}/{bAes} lzss={aLzss}/{bLzss}");
				}
			}
		}

		Console.WriteLine("=== compare summary ===");
		Console.WriteLine($"  missing in B      : {missing}");
		Console.WriteLine($"  extracted size diff: {sizeDiff}");
		Console.WriteLine($"  chunk count diff  : {chunkDiff}");
		Console.WriteLine($"  transform diff    : {transformDiff}");
		Console.WriteLine($"  CONTENT diff      : {contentDiff}");
		return 0;
	}

	private static async Task<byte[]> HashItemAsync(IFileSystem fs, NefsTransformer transformer, NefsItem item)
	{
		if (item.Type != NefsItemType.File)
		{
			return Array.Empty<byte>();
		}

		using var input = item.DataSource.OpenRead(fs);
		using var output = new MemoryStream();
		await transformer.DetransformAsync(
			input,
			item.DataSource.Offset,
			output,
			0,
			item.ExtractedSize,
			item.DataSource.Size.Chunks,
			new NefsProgress());
		return System.Security.Cryptography.SHA256.HashData(output.ToArray());
	}

	private static async Task<NefsArchive> LoadArchive(string path)
	{
		var fs = new FileSystem();
		var reader = new NefsReader(fs);
		var before = DateTime.Now;
		var archive = await reader.ReadArchiveAsync(path, new NefsProgress());
		Console.WriteLine($"loaded in {(DateTime.Now - before).TotalSeconds:F1}s");
		return archive;
	}

	private static void PrintInfo(string path, NefsArchive archive)
	{
		var fi = new FileInfo(path);
		Console.WriteLine("--- header summary ---");
		Console.WriteLine($"  file: {path}");
		Console.WriteLine($"  file size on disk: {fi.Length:N0} bytes");
		Console.WriteLine($"  version: {archive.Header.Version}");
		Console.WriteLine($"  num entries: {archive.Header.NumEntries}");
		Console.WriteLine($"  block size: 0x{archive.Header.BlockSize:X}");
		Console.WriteLine($"  split size: 0x{archive.Header.SplitSize:X}");
		Console.WriteLine($"  volumes: {archive.Header.Volumes.Count}");
		for (var i = 0; i < archive.Header.Volumes.Count; i++)
		{
			var v = archive.Header.Volumes[i];
			Console.WriteLine($"    [{i}] name='{v.Name}' size=0x{v.Size:X} ({v.Size:N0}) dataOffset=0x{v.DataOffset:X}");
		}

		var perVolItems = archive.Items.EnumerateById().GroupBy(x => x.Attributes.Volume)
			.OrderBy(g => g.Key)
			.ToList();
		Console.WriteLine("--- items per volume ---");
		foreach (var g in perVolItems)
		{
			var dataBytes = g.Sum(x => (decimal)x.CompressedSize);
			Console.WriteLine($"  vol {g.Key}: {g.Count()} items, {dataBytes:N0} compressed bytes");
		}

		var blockCount = archive.Header is VictorBush.Ego.NefsLib.Header.Version150.NefsHeader151 h151
			? h151.BlockTable.Entries.Count
			: 0;
		Console.WriteLine($"  block table entries: {blockCount}");
	}

	private static void PrintUsage()
	{
		Console.WriteLine("usage:");
		Console.WriteLine("  nefstool info <archive.nfs>");
		Console.WriteLine("  nefstool save <archive.nfs> <out.nfs>");
		Console.WriteLine("  nefstool cmp <a.nfs> <b.nfs>");
		Console.WriteLine("  nefstool list <archive.nfs> [path-filter]");
		Console.WriteLine("  nefstool grep <archive.nfs> <text> [max-mb]");
		Console.WriteLine("  nefstool grepci <archive.nfs> <text> [max-mb]");
		Console.WriteLine("  nefstool extract <archive.nfs> <name|#id> <out-file>");
		Console.WriteLine("  nefstool extract-all <archive.nfs> <out-dir> [path-filter]");
		Console.WriteLine("  nefstool replace <archive.nfs> <out.nfs> <name|#id> <file> [...]");
		Console.WriteLine("  nefstool add <archive.nfs> <out.nfs> <path> <file> [...]");
		Console.WriteLine();
		Console.WriteLine("grep max-mb defaults to 256; use 0 for no limit.");
		Console.WriteLine("add paths may target existing directories, e.g. 'language/file.lng'.");
	}

	private static int PrintUsageAndFail()
	{
		Console.WriteLine("bad arguments");
		PrintUsage();
		return 2;
	}

	private static string NormalizeArchivePath(string path)
	{
		return path.Replace('\\', '/').Trim('/');
	}

	private static NefsItem? FindDirectory(NefsArchive archive, string path)
	{
		var normalized = NormalizeArchivePath(path);
		foreach (var item in archive.Items.EnumerateById().Where(x => x.Type == NefsItemType.Directory))
		{
			var itemPath = NormalizeArchivePath(archive.Items.GetItemFilePath(item.Id));
			if (string.Equals(itemPath, normalized, StringComparison.OrdinalIgnoreCase))
			{
				return item;
			}
		}

		return null;
	}

	private static List<NefsItem> ResolveItems(NefsArchive archive, string selector)
	{
		if (selector.StartsWith("#", StringComparison.Ordinal))
		{
			if (!uint.TryParse(selector[1..], out var id))
			{
				return [];
			}

			var item = archive.Items.EnumerateById()
				.FirstOrDefault(x => x.Type == NefsItemType.File && x.Id.Value == id);
			return item is null ? [] : [item];
		}

		var normalized = NormalizeArchivePath(selector);
		return archive.Items.EnumerateById()
			.Where(x => x.Type == NefsItemType.File)
			.Where(x => string.Equals(NormalizeArchivePath(archive.Items.GetItemFilePath(x.Id)), normalized,
				            StringComparison.OrdinalIgnoreCase) ||
			            string.Equals(x.FileName, selector, StringComparison.OrdinalIgnoreCase))
			.ToList();
	}

	private static void PrintMatchError(string command, string selector, IReadOnlyList<NefsItem> matches)
	{
		if (matches.Count == 0)
		{
			Console.WriteLine($"{command}: item not found: '{selector}'");
			return;
		}

		Console.WriteLine($"{command}: selector '{selector}' matched {matches.Count} items; use #id:");
		foreach (var item in matches)
		{
			Console.WriteLine($"  #{item.Id.Value} '{item.FileName}'");
		}
	}

	private static string SafeOutputPath(string root, string relativePath)
	{
		var normalized = relativePath
			.Replace('/', Path.DirectorySeparatorChar)
			.Replace('\\', Path.DirectorySeparatorChar);
		var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
		var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException($"refusing to write outside output directory: '{relativePath}'");
		}

		return fullPath;
	}
}