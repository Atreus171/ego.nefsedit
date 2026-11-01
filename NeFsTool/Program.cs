using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VictorBush.Ego.NefsLib;
using VictorBush.Ego.NefsLib.Header.Version150;
using VictorBush.Ego.NefsLib.IO;
using VictorBush.Ego.NefsLib.Item;
using VictorBush.Ego.NefsLib.Progress;

if (args.Length < 1)
{
	PrintUsage();
	return 1;
}

switch (args[0])
{
	case "list":
		return await ListCmd(args);
	case "extract":
		return await ExtractCmd(args);
	case "patch":
		return await PatchCmd(args);
	case "diag":
		return await DiagCmd(args);
	case "verify":
		return await VerifyCmd(args);
	case "diff":
		return await DiffCmd(args);
	case "intro":
		return await IntroCmd(args);
	case "headerdiff":
		return await HeaderDiffCmd(args);
	case "flip":
		return await FlipCmd(args);
	case "introtweak":
		return await IntrotweakCmd(args);
	case "crack":
		return await CrackCmd(args);
	case "sweep":
		return await SweepCmd(args);
	case "datacrc":
		return await DataCrcCmd(args);
	case "blocksum":
		return await BlockSumCmd(args);
	case "tables":
		return await TablesCmd(args);
	default:
		PrintUsage();
		return 1;
}

static void PrintUsage()
{
	Console.WriteLine("usage:");
	Console.WriteLine("  nefstool list <volume0.nfs> [filter]");
	Console.WriteLine("  nefstool extract <volume0.nfs> <outdir> [filter]");
	Console.WriteLine("  nefstool patch <volume0.nfs> <new_language_eng.lng> <new_language_use.lng> [--apply] [--inplace|--relocate|--relayout] [--origlangs] [--origorder]");
	Console.WriteLine("  nefstool diag <volume0.nfs>");
	Console.WriteLine("  nefstool verify <volume0.nfs>");
	Console.WriteLine("  nefstool diff <fileA> <fileB>");
	Console.WriteLine("  nefstool intro <volume0.nfs>");
	Console.WriteLine("  nefstool headerdiff <fileA> <fileB>");
	Console.WriteLine("  nefstool flip <volume0.nfs> <hexOffset> <hexByte>");
	Console.WriteLine("  nefstool introtweak <volume0.nfs> <dwordIdx> <newHexValue>");
	Console.WriteLine("  nefstool crack <volume0.nfs>");
	Console.WriteLine("  nefstool sweep <volume0.nfs>");
	Console.WriteLine("  nefstool datacrc <volume0.nfs>");
	Console.WriteLine("  nefstool blocksum <volume0.nfs>");
}

static async Task<int> ListCmd(string[] a)
{
	if (a.Length < 2)
	{
		PrintUsage();
		return 1;
	}

	var path = Path.GetFullPath(a[1]);
	var filter = a.Length > 2 ? a[2] : null;
	var arch = await Open(path);
	var h = (NefsHeader151)arch.Header;

	Console.WriteLine($"File: {path}");
	Console.WriteLine($"Version: {h.Version}");
	Console.WriteLine($"IsLittleEndian: {h.IsLittleEndian}");
	Console.WriteLine($"IsEncrypted: {h.IsEncrypted}");
	Console.WriteLine($"BlockSize: {h.BlockSize}");
	Console.WriteLine($"SplitSize: {h.SplitSize}");
	Console.WriteLine($"NumEntries: {h.NumEntries}");
	Console.WriteLine(
		$"entryStart={h.Intro.EntryTableStart} sharedStart={h.Intro.SharedEntryInfoTableStart} " +
		$"nameStart={h.Intro.NameTableStart} blockStart={h.Intro.BlockTableStart} " +
		$"volumeInfoStart={h.Intro.VolumeInfoTableStart}");
	Console.WriteLine("Volumes:");
	foreach (var v in arch.Header.Volumes)
	{
		Console.WriteLine($"  name={v.Name} dataOffset={v.DataOffset} size={v.Size}");
	}
	Console.WriteLine();
	Console.WriteLine("ITEMS:");
	foreach (var item in arch.Items.EnumerateById())
	{
		var name = arch.Items.GetItemFilePath(item.Id);
		if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
		{
			continue;
		}

		var type = item.Type == NefsItemType.Directory ? "DIR " : "FILE";
		var dup = item.IsDuplicate ? " DUP" : "";
		var transform = item.Transform is null
			? "none"
			: item.Transform.IsZlibCompressed
				? "zlib"
				: item.Transform.IsLzssCompressed
					? "lzss"
					: item.Transform.IsAesEncrypted
						? "aes"
						: "?";
		Console.WriteLine(
			$"{item.Id.Value,5} {type,-4} vol={item.Attributes.Volume} off={item.DataSource.Offset,10} " +
			$"ext={item.ExtractedSize,10} comp={item.CompressedSize,10} blk={item.DataSource.Size.Chunks.Count,4} " +
			$"tr={transform,-5}{dup} {name}");
	}

	return 0;
}

static async Task<int> ExtractCmd(string[] a)
{
	if (a.Length < 3)
	{
		PrintUsage();
		return 1;
	}

	var arch = await Open(Path.GetFullPath(a[1]));
	var outDir = Path.GetFullPath(a[2]);
	var filter = a.Length > 3 ? a[3] : null;

	Directory.CreateDirectory(outDir);
	foreach (var item in arch.Items.EnumerateById())
	{
		if (item.Type == NefsItemType.Directory || item.IsDuplicate)
		{
			continue;
		}

		var path = arch.Items.GetItemFilePath(item.Id);
		if (filter is not null && !path.Contains(filter, StringComparison.OrdinalIgnoreCase))
		{
			continue;
		}

		var safeName = path.Replace("\\", "_").Replace("/", "_");
		var (_, content) = ExtractItemAsync(arch, item);
		await File.WriteAllBytesAsync(Path.Combine(outDir, safeName), content);
		Console.WriteLine($"extracted {item.Id.Value} {path} ({content.Length} bytes)");
	}

	return 0;
}

static async Task<int> PatchCmd(string[] a)
{
	if (a.Length < 4)
	{
		PrintUsage();
		return 1;
	}

	var volumeFile = Path.GetFullPath(a[1]);
	var engNew = Path.GetFullPath(a[2]);
	var useNew = Path.GetFullPath(a[3]);
	var apply = a.Contains("--apply");
	var inplace = a.Contains("--inplace");
	var relocate = a.Contains("--relocate");
	var relayout = a.Contains("--relayout");
	var origlangs = a.Contains("--origlangs");
	var origorder = a.Contains("--origorder");
	var verbatim = a.Contains("--verbatim");

	var reader = new NefsReader(new System.IO.Abstractions.FileSystem());
	var arch = await reader.ReadArchiveAsync(volumeFile, new NefsProgress());
	var h = (NefsHeader151)arch.Header;

	if (h.IsLittleEndian)
	{
		Console.Error.WriteLine("Expected big-endian (Xbox 360) archive.");
		return 1;
	}

	Console.WriteLine(
		$"Header: {h.Version} entries={h.NumEntries} blockSize={h.BlockSize} " +
		$"entryStart={h.Intro.EntryTableStart} sharedStart={h.Intro.SharedEntryInfoTableStart} " +
		$"blockStart={h.Intro.BlockTableStart} volumeInfoStart={h.Intro.VolumeInfoTableStart}");

	var engItem = FindItem(arch, "language_eng.lng");
	var useItem = FindItem(arch, "language_use.lng");
	if (engItem is null || useItem is null)
	{
		Console.Error.WriteLine("Could not find language files in archive.");
		return 1;
	}

	Console.WriteLine($"eng id={engItem.Id.Value} offset={engItem.DataSource.Offset}");
	Console.WriteLine($"use id={useItem.Id.Value} offset={useItem.DataSource.Offset}");

	var engBytes = File.ReadAllBytes(engNew);
	var useBytes = File.ReadAllBytes(useNew);

	if (NumChunks(engBytes.Length, h.BlockSize) != NumChunks(engItem.ExtractedSize, h.BlockSize) ||
		NumChunks(useBytes.Length, h.BlockSize) != NumChunks(useItem.ExtractedSize, h.BlockSize))
	{
		Console.Error.WriteLine("Block count would change; in-place patch not supported for this case.");
		return 1;
	}

	var (compEng, endsEng) = CompressItem(engBytes, (int)h.BlockSize);
	var (compUse, endsUse) = CompressItem(useBytes, (int)h.BlockSize);
	Console.WriteLine(
		$"new eng size={engBytes.Length} chunks={NumChunks(engBytes.Length, h.BlockSize)} " +
		$"compressed={compEng.Length} ends={string.Join(",", endsEng)}");
	Console.WriteLine(
		$"new use size={useBytes.Length} chunks={NumChunks(useBytes.Length, h.BlockSize)} " +
		$"compressed={compUse.Length} ends={string.Join(",", endsUse)}");

	using var fs = new FileStream(volumeFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
	long entryOff = h.Intro.EntryTableStart;
	var eEng = ReadEntry(fs, entryOff, (long)engItem.Id.Value);
	var eUse = ReadEntry(fs, entryOff, (long)useItem.Id.Value);

	if (eEng.Start != (ulong)engItem.DataSource.Offset || eUse.Start != (ulong)useItem.DataSource.Offset)
	{
		Console.Error.WriteLine(
			$"Entry table mismatch: eng header={eEng.Start} item={engItem.DataSource.Offset}; " +
			$"use header={eUse.Start} item={useItem.DataSource.Offset}. Aborting.");
		return 1;
	}

	if (relocate)
	{
		var fre = FindItem(arch, "language_fre.lng");
		var spa = FindItem(arch, "language_spa.lng");
		if (fre is null || spa is null)
		{
			Console.Error.WriteLine("Could not find language_fre/spa items for relocation region.");
			return 1;
		}

		long holeStart = (long)fre.DataSource.Offset;
		long holeEnd = (long)spa.DataSource.Offset;
		long origEng = (long)engItem.DataSource.Offset;
		long origUse = (long)useItem.DataSource.Offset;
		int engLen = (int)engItem.CompressedSize;
		int useLen = (int)useItem.CompressedSize;

		Console.WriteLine($"relocate: eng {engLen}B at {origEng} -> {holeStart}; use {useLen}B at {origUse} -> {holeStart + engLen}");

		if (engLen + (long)useLen > holeEnd - holeStart)
		{
			Console.Error.WriteLine($"Relocation region too small: need {engLen + useLen} but only {holeEnd - holeStart} available.");
			return 1;
		}

		var (_, origEngBytes) = ExtractItemAsync(arch, engItem);
		var (_, origUseBytes) = ExtractItemAsync(arch, useItem);

		if (!apply)
		{
			Console.WriteLine("Dry run: no changes written. Re-run with --apply to actually patch.");
			return 0;
		}

		var engComp = new byte[engLen];
		fs.Position = origEng;
		fs.ReadExactly(engComp, 0, engLen);
		var useComp = new byte[useLen];
		fs.Position = origUse;
		fs.ReadExactly(useComp, 0, useLen);

		fs.Position = holeStart;
		fs.Write(engComp);
		fs.Position = holeStart + engLen;
		fs.Write(useComp);

		WriteStart(fs, entryOff + (long)engItem.Id.Value * 24, (ulong)holeStart);
		WriteStart(fs, entryOff + (long)useItem.Id.Value * 24, (ulong)(holeStart + engLen));

		fs.Flush(true);
		fs.Dispose();
		Console.WriteLine("Relocation written. Verifying...");

		var relocOk = await VerifyExtractAsync(reader, volumeFile, origEngBytes, origUseBytes);
		Console.WriteLine(relocOk
			? "OK: items relocated and still verify against original content."
			: "FAIL: validation did not pass.");
		return relocOk ? 0 : 1;
	}

	if (relayout)
	{
		var langOrder = origorder
			? new[] { "language_eng.lng", "language_use.lng", "language_fre.lng", "language_ger.lng", "language_ita.lng", "language_spa.lng" }
			: new[] { "language_eng.lng", "language_fre.lng", "language_ger.lng", "language_ita.lng", "language_spa.lng", "language_use.lng" };

		var items = new NefsItem[langOrder.Length];
		for (var i = 0; i < langOrder.Length; i++)
		{
			items[i] = FindItem(arch, langOrder[i]);
			if (items[i] is null)
			{
				Console.Error.WriteLine($"Could not find {langOrder[i]} in archive.");
				return 1;
			}
		}

		long layoutStart = (long)items[0].DataSource.Offset;
		long regionEnd = 0;
		for (var i = 0; i < items.Length; i++)
			regionEnd = Math.Max(regionEnd, (long)items[i].DataSource.Offset + (long)items[i].CompressedSize);
		Console.WriteLine(
			$"relayout ({(origorder ? "orig-order" : "relayout-order")}): region [{layoutStart}, {regionEnd}) = {regionEnd - layoutStart} bytes");

		var payloads = new List<(byte[] Data, uint[] Ends, byte[] Original, string Name)>();
		for (var i = 0; i < langOrder.Length; i++)
		{
			if (verbatim)
			{
				var srcItem = items[i];
				var origStart = (long)srcItem.DataSource.Offset;
				var srcEntry = ReadEntry(fs, entryOff, (long)srcItem.Id.Value);
				var comp = new byte[(int)srcItem.CompressedSize];
				fs.Position = origStart;
				fs.ReadExactly(comp, 0, comp.Length);
				var ends = ReadVerbatimEnds(fs, h, (long)srcEntry.FirstBlock, (int)srcItem.ExtractedSize, (int)h.BlockSize);
				var (_, original) = ExtractItemAsync(arch, srcItem);
				payloads.Add((comp, ends, original, langOrder[i]));
				Console.WriteLine($"  {langOrder[i]}: VERBATIM {comp.Length} bytes from {origStart} ends={string.Join(",", ends)}");
				continue;
			}

			byte[] content;
			switch (langOrder[i])
			{
				case "language_eng.lng":
					content = engBytes;
					break;
				case "language_use.lng":
					content = useBytes;
					break;
				default:
					if (origlangs)
					{
						var (_, extracted) = ExtractItemAsync(arch, items[i]);
						content = extracted;
					}
					else
					{
						content = new byte[items[i].ExtractedSize];
					}
					break;
			}

			var (vc, ve) = CompressItem(content, (int)h.BlockSize);
			payloads.Add((vc, ve, content, langOrder[i]));
			Console.WriteLine(
				$"  {langOrder[i]}: {content.Length} bytes ({(langOrder[i] is "language_fre.lng" or "language_ger.lng" or "language_ita.lng" or "language_spa.lng" ? (origlangs ? "original" : "zeros") : "new")}) -> compressed={vc.Length} ends={string.Join(",", ve)}");
		}

		var starts = new long[payloads.Count];
		long cursor = layoutStart;
		for (var i = 0; i < payloads.Count; i++)
		{
			starts[i] = cursor;
			cursor += payloads[i].Data.Length;
			Console.WriteLine(
				$"  {langOrder[i]}: start={starts[i]} comp={payloads[i].Data.Length} ends={string.Join(",", payloads[i].Ends)}");
		}

		long shift = Math.Max(0, cursor - regionEnd);
		Console.WriteLine(
			$"  languages total={cursor - layoutStart} bytes; post-language region must shift right by {shift} bytes");

		var langSet = new HashSet<string>(langOrder, StringComparer.OrdinalIgnoreCase);
		var tail = new List<(NefsItem Item, long OrigStart)>();
		foreach (var it in arch.Items.EnumerateById())
		{
			if (it.Attributes.Volume != 0u || it.Type == NefsItemType.Directory || it.IsDuplicate || langSet.Contains(it.FileName))
			{
				continue;
			}

			if ((long)it.DataSource.Offset >= regionEnd)
			{
				tail.Add((it, (long)it.DataSource.Offset));
			}
		}

		var tailSamples = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		foreach (var (it, _) in tail.Take(10))
		{
			var name = arch.Items.GetItemFilePath(it.Id);
			var (_, content) = ExtractItemAsync(arch, it);
			tailSamples[name] = content;
		}

		if (!apply)
		{
			Console.WriteLine($"  would shift {tail.Count} post-language items");
			Console.WriteLine("Dry run: no changes written. Re-run with --apply to actually patch.");
			return 0;
		}

		if (shift > 0)
		{
			const int Chunk = 1 << 20;
			var buf = new byte[Chunk];
			long remaining = fs.Length - regionEnd;
			while (remaining > 0)
			{
				var n = (int)Math.Min(Chunk, remaining);
				var srcPos = regionEnd + remaining - n;
				fs.Position = srcPos;
				fs.ReadExactly(buf, 0, n);
				fs.Position = srcPos + shift;
				fs.Write(buf, 0, n);
				remaining -= n;
			}
		}

		for (var i = 0; i < payloads.Count; i++)
		{
			fs.Position = starts[i];
			fs.Write(payloads[i].Data);
		}

		for (var i = 0; i < payloads.Count; i++)
		{
			var item = items[i];
			var e = ReadEntry(fs, entryOff, (long)item.Id.Value);
			var data = payloads[i].Data;
			var ends = payloads[i].Ends;

			WriteStart(fs, entryOff + (long)item.Id.Value * 24, (ulong)starts[i]);

			if (!verbatim)
			{
				for (var k = 0; k < ends.Length; k++)
				{
					var blockOff = k > 0 ? ends[k - 1] : 0u;
					var row = h.Intro.BlockTableStart + (long)e.FirstBlock * 8 + k * 8;
					WriteUInt32At(fs, row, ends[k]);
					WriteUInt16At(fs, row + 6, Checksum16(data.AsSpan((int)blockOff, (int)(ends[k] - blockOff)).ToArray()));
				}
			}

			WriteUInt32At(fs, h.Intro.SharedEntryInfoTableStart + (long)e.SharedInfo * 28 + 16, (uint)payloads[i].Original.Length);
		}

		if (shift > 0)
		{
			foreach (var (it, origStart) in tail)
			{
				WriteStart(fs, entryOff + (long)it.Id.Value * 24, (ulong)(origStart + shift));
			}

			long origLen = new FileInfo(volumeFile).Length;
			long volInfo = h.Intro.VolumeInfoTableStart;
			WriteUInt64At(fs, volInfo, ReadUInt64At(fs, volInfo) + (ulong)shift);
			fs.SetLength(origLen + shift);
		}

		fs.Flush(true);
		fs.Dispose();

		Console.WriteLine("Relayout written. Verifying...");
		var relayoutOk = await VerifyRelayoutAsync(reader, volumeFile, langOrder, payloads.Select(p => p.Original).ToArray());
		relayoutOk = await VerifyNamedItemsAsync(reader, volumeFile, tailSamples) && relayoutOk;
		Console.WriteLine(relayoutOk ? "OK: relayout archive verifies." : "FAIL: validation did not pass.");
		return relayoutOk ? 0 : 1;
	}

	long fileLen = new FileInfo(volumeFile).Length;
	var used = new List<(long start, long end)>();
	foreach (var it in arch.Items.EnumerateById())
	{
		if (it.Attributes.Volume != 0u || it.Type == NefsItemType.Directory)
		{
			continue;
		}

		used.Add((it.DataSource.Offset, it.DataSource.Offset + (long)it.CompressedSize));
	}

	long minStart = (long)arch.Header.Volumes[0].DataOffset;
	long place;
	var appendMode = false;
	if (inplace)
	{
		var fre = FindItem(arch, "language_fre.lng");
		var spa = FindItem(arch, "language_spa.lng");
		if (fre is null || spa is null)
		{
			Console.Error.WriteLine("Could not find language_fre/spa items for in-place region.");
			return 1;
		}

		var holeStart = (long)fre.DataSource.Offset;
		var holeEnd = (long)spa.DataSource.Offset;
		if (compEng.Length + compUse.Length > holeEnd - holeStart)
		{
			Console.Error.WriteLine(
				$"In-place region too small: need {compEng.Length + compUse.Length} but only {holeEnd - holeStart} available.");
			return 1;
		}

		place = holeStart;
		Console.WriteLine(
			$"in-place overwriting language region [{holeStart}, {holeEnd}) with {compEng.Length + compUse.Length} bytes " +
			$"(fre/ger/ita data will be corrupted; only the active language is read)");
	}
	else
	{
		place = FindFreeSpace(used, fileLen, minStart, compEng.Length + compUse.Length);
		appendMode = place < 0;
		if (appendMode)
		{
			place = fileLen;
		}
	}

	long placeEng = place;
	long placeUse = place + compEng.Length;
	Console.WriteLine(
		$"{(inplace ? "in-place" : appendMode ? "appending" : "free space")} {compEng.Length + compUse.Length} bytes at {place} " +
		$"(appendMode={(appendMode ? 1 : 0)})");

	if (!apply)
	{
		Console.WriteLine("Dry run: no changes written. Re-run with --apply to actually patch.");
		return 0;
	}

	fs.Position = placeEng;
	await fs.WriteAsync(compEng);
	fs.Position = placeUse;
	await fs.WriteAsync(compUse);

	WriteStart(fs, entryOff + (long)engItem.Id.Value * 24, (ulong)placeEng);
	WriteStart(fs, entryOff + (long)useItem.Id.Value * 24, (ulong)placeUse);

	WriteUInt32At(fs, h.Intro.SharedEntryInfoTableStart + (long)eEng.SharedInfo * 28 + 16, (uint)engBytes.Length);
	WriteUInt32At(fs, h.Intro.SharedEntryInfoTableStart + (long)eUse.SharedInfo * 28 + 16, (uint)useBytes.Length);

	for (var k = 0; k < endsEng.Length; k++)
	{
		var off = k > 0 ? endsEng[k - 1] : 0u;
		WriteUInt32At(fs, h.Intro.BlockTableStart + (long)eEng.FirstBlock * 8 + k * 8, endsEng[k]);
		WriteUInt16At(fs, h.Intro.BlockTableStart + (long)eEng.FirstBlock * 8 + k * 8 + 6,
			Checksum16(compEng.AsSpan((int)off, (int)(endsEng[k] - off)).ToArray()));
	}

	for (var k = 0; k < endsUse.Length; k++)
	{
		var off = k > 0 ? endsUse[k - 1] : 0u;
		WriteUInt32At(fs, h.Intro.BlockTableStart + (long)eUse.FirstBlock * 8 + k * 8, endsUse[k]);
		WriteUInt16At(fs, h.Intro.BlockTableStart + (long)eUse.FirstBlock * 8 + k * 8 + 6,
			Checksum16(compUse.AsSpan((int)off, (int)(endsUse[k] - off)).ToArray()));
	}

	if (appendMode)
	{
		WriteUInt64At(fs, h.Intro.VolumeInfoTableStart + 0 * 16 + 0, (ulong)(fileLen + compEng.Length + compUse.Length));
	}

	fs.Flush(true);
	fs.Dispose();

	Console.WriteLine("Patch written. Verifying...");
	using (var verifyFs = new FileStream(volumeFile, FileMode.Open, FileAccess.Read, FileShare.Read))
	{
		var veEng = ReadEntry(verifyFs, entryOff, (long)engItem.Id.Value);
		var veUse = ReadEntry(verifyFs, entryOff, (long)useItem.Id.Value);
		Console.WriteLine($"entry.readback eng start={veEng.Start} use start={veUse.Start}");
		Console.WriteLine(
			$"shared.readback eng size={ReadUInt32At(verifyFs, h.Intro.SharedEntryInfoTableStart + (long)eEng.SharedInfo * 28 + 16)} " +
			$"use size={ReadUInt32At(verifyFs, h.Intro.SharedEntryInfoTableStart + (long)eUse.SharedInfo * 28 + 16)}");
		var readEngEnds = string.Join(",", endsEng.Select((_, k) =>
			ReadUInt32At(verifyFs, h.Intro.BlockTableStart + (long)eEng.FirstBlock * 8 + k * 8)));
		var readUseEnds = string.Join(",", endsUse.Select((_, k) =>
			ReadUInt32At(verifyFs, h.Intro.BlockTableStart + (long)eUse.FirstBlock * 8 + k * 8)));
		Console.WriteLine($"blocks.readback eng ends={readEngEnds} use ends={readUseEnds}");
		var readEngChk = string.Join(",", endsEng.Select((_, k) =>
			ReadUInt16At(verifyFs, h.Intro.BlockTableStart + (long)eEng.FirstBlock * 8 + k * 8 + 6)));
		var readUseChk = string.Join(",", endsUse.Select((_, k) =>
			ReadUInt16At(verifyFs, h.Intro.BlockTableStart + (long)eUse.FirstBlock * 8 + k * 8 + 6)));
		Console.WriteLine($"blocks.readback eng checksums={readEngChk} use checksums={readUseChk}");
	}

	var ok = await VerifyExtractAsync(reader, volumeFile, engBytes, useBytes);

	Console.WriteLine(ok
		? "OK: archive patched and language items verify against new PT-BR files."
		: "FAIL: validation did not pass.");
	return ok ? 0 : 1;
}

static async Task<NefsArchive> Open(string filePath)
{
	var reader = new NefsReader(new System.IO.Abstractions.FileSystem());
	var arch = await reader.ReadArchiveAsync(filePath, new NefsProgress());
	if (arch.Header is not NefsHeader151)
	{
		throw new InvalidOperationException("Only NeFS 1.5.1 archives are supported by this tool.");
	}

	return arch;
}

static async Task<int> DiagCmd(string[] a)
{
	var volumeFile = Path.GetFullPath(a[1]);
	var reader = new NefsReader(new System.IO.Abstractions.FileSystem());
	var arch = await reader.ReadArchiveAsync(volumeFile, new NefsProgress());
	var h = (NefsHeader151)arch.Header;

	using var fs = new FileStream(volumeFile, FileMode.Open, FileAccess.Read, FileShare.Read);
	long entryOff = h.Intro.EntryTableStart;
	var items = arch.Items.EnumerateById().ToList();
	var dys = new List<string>();

	for (var i = 0; i < items.Count; i++)
	{
		var item = items[i];
		var e = ReadEntry(fs, entryOff, item.Id.Value);
		if (item.Type == NefsItemType.Directory || item.IsDuplicate)
		{
			continue;
		}

		var libChunks = NumChunks(item.ExtractedSize, h.BlockSize);
		uint nextRawFirst = uint.MaxValue;
		for (var j = i + 1; j < items.Count; j++)
		{
			if (items[j].Type != NefsItemType.Directory && !items[j].IsDuplicate)
			{
				var ej = ReadEntry(fs, entryOff, items[j].Id.Value);
				nextRawFirst = ej.FirstBlock;
				break;
			}
		}

		var actual = nextRawFirst == uint.MaxValue ? 0u : nextRawFirst - e.FirstBlock;
		if (actual != (uint)libChunks)
		{
			dys.Add(
				$"id={item.Id.Value} path={arch.Items.GetItemFilePath(item.Id)} " +
				$"libExt={item.ExtractedSize} libChunks={libChunks} rawFirst={e.FirstBlock} " +
				$"actualNext={actual} delta={(long)actual - libChunks}");
		}
	}

	Console.WriteLine($"firstBlock-gap discrepancies: {dys.Count}");
	foreach (var d in dys.Take(20))
	{
		Console.WriteLine(d);
	}

	return 0;
}

static async Task<int> VerifyCmd(string[] a)
{
	if (a.Length < 2)
	{
		PrintUsage();
		return 1;
	}

	var volumeFile = Path.GetFullPath(a[1]);
	var reader = new NefsReader(new System.IO.Abstractions.FileSystem());
	var arch = await reader.ReadArchiveAsync(volumeFile, new NefsProgress());
	var h = (NefsHeader151)arch.Header;

	using var fs = new FileStream(volumeFile, FileMode.Open, FileAccess.Read, FileShare.Read);
	long entryOff = h.Intro.EntryTableStart;

	foreach (var name in new[] { "language_eng.lng", "language_use.lng" })
	{
		var item = FindItem(arch, name);
		if (item is null)
		{
			Console.WriteLine($"{name}: NOT FOUND");
			continue;
		}

		var entry = ReadEntry(fs, entryOff, item.Id.Value);
		var (_, raw) = ExtractItemAsync(arch, item);
		var nBlocks = NumChunks(item.ExtractedSize, h.BlockSize);

		Console.WriteLine($"=== {name}: id={item.Id.Value} ext={raw.Length} comp={item.CompressedSize} blocks={nBlocks} firstBlock={entry.FirstBlock} ===");

		// Compute checksum over RAW chunks
		var rawChk = new ushort[nBlocks];
		for (var k = 0; k < nBlocks; k++)
		{
			var off = k * (int)h.BlockSize;
			var len = Math.Min((int)h.BlockSize, raw.Length - off);
			rawChk[k] = Checksum16(raw.AsSpan(off, len).ToArray());
		}

		// Compute checksum over COMPRESSED chunks
		var compChk = new ushort[nBlocks];
		long lastCum = 0;
		for (var k = 0; k < nBlocks; k++)
		{
			var cum = item.DataSource.Size.Chunks[k].CumulativeSize;
			var len = (int)(cum - lastCum);
			var buf = new byte[len];
			fs.Position = item.DataSource.Offset + lastCum;
			fs.ReadExactly(buf, 0, len);
			compChk[k] = Checksum16(buf);
			lastCum = cum;
		}

		// Stored checksums
		var stored = new ushort[nBlocks];
		for (var k = 0; k < nBlocks; k++)
		{
			stored[k] = ReadUInt16At(fs, h.Intro.BlockTableStart + (long)entry.FirstBlock * 8 + k * 8 + 6);
		}

		var rawMatch = 0;
		var compMatch = 0;
		for (var k = 0; k < nBlocks; k++)
		{
			if (rawChk[k] == stored[k]) rawMatch++;
			if (compChk[k] == stored[k]) compMatch++;
			Console.WriteLine(
				$"  blk{k}: stored={stored[k]:X4} raw={rawChk[k]:X4}{(rawChk[k] == stored[k] ? " *" : "")} comp={compChk[k]:X4}{(compChk[k] == stored[k] ? " *" : "")}");
		}

		Console.WriteLine($"  MATCH: raw={rawMatch}/{nBlocks} comp={compMatch}/{nBlocks}");
	}

	return 0;
}

static async Task<int> DiffCmd(string[] a)
{
	if (a.Length < 3)
	{
		PrintUsage();
		return 1;
	}

	var fa = Path.GetFullPath(a[1]);
	var fb = Path.GetFullPath(a[2]);
	using var sa = File.OpenRead(fa);
	using var sb = File.OpenRead(fb);
	var la = sa.Length;
	var lb = sb.Length;
	Console.WriteLine($"A={fa} ({la})");
	Console.WriteLine($"B={fb} ({lb})");
	Console.WriteLine($"length delta={lb - la}");

	var bufA = new byte[1 << 20];
	var bufB = new byte[1 << 20];
	long minLen = Math.Min(la, lb);
	long pos = 0;
	var ranges = new List<(long start, long end)>();
	long rangeStart = -1;
	while (pos < minLen)
	{
		var n = (int)Math.Min(bufA.Length, minLen - pos);
		sa.Position = pos;
		sb.Position = pos;
		sa.ReadExactly(bufA, 0, n);
		sb.ReadExactly(bufB, 0, n);
		for (var i = 0; i < n; i++)
		{
			if (bufA[i] != bufB[i])
			{
				if (rangeStart < 0) rangeStart = pos + i;
			}
			else if (rangeStart >= 0)
			{
				ranges.Add((rangeStart, pos + i));
				rangeStart = -1;
			}
		}
		pos += n;
	}
	if (rangeStart >= 0) ranges.Add((rangeStart, minLen));

	if (lb != la)
	{
		Console.WriteLine($"  tail extension [{minLen}, {lb}) length {lb - minLen} present only in B");
	}

	Console.WriteLine($"different ranges: {ranges.Count}");
	foreach (var (s, e) in ranges)
	{
		var hex = s.ToString("X8");
		Console.WriteLine($"  [{s}, {e}) len={e - s} (0x{hex})");
	}

	return 0;
}

static int DecodeXorIntro(ReadOnlySpan<byte> buffer, Span<uint> uintBuf)
{
	MemoryMarshal.Cast<byte, uint>(buffer).CopyTo(uintBuf);

	uintBuf[14] ^= uintBuf[5];
	uintBuf[5] ^= uintBuf[2];
	uintBuf[2] ^= uintBuf[4];
	uintBuf[4] ^= uintBuf[7];
	uintBuf[7] ^= uintBuf[3];
	uintBuf[3] ^= uintBuf[9];
	uintBuf[9] ^= uintBuf[10];
	uintBuf[10] ^= uintBuf[1];
	uintBuf[1] ^= uintBuf[13];
	uintBuf[13] ^= uintBuf[11];
	uintBuf[11] ^= uintBuf[0];
	uintBuf[0] ^= uintBuf[12];
	uintBuf[12] ^= uintBuf[6];
	uintBuf[6] ^= uintBuf[8];
	uintBuf[8] ^= uintBuf[14];

	var mod = uintBuf[14];
	for (var i = 15; i < 31; ++i)
	{
		uintBuf[i] ^= mod;
	}

	return 0;
}

static void EncodeXorIntro(Span<byte> buffer)
{
	var uintBuf = MemoryMarshal.Cast<byte, uint>(buffer);

	var mod = uintBuf[14];
	for (var i = 15; i < 31; ++i)
	{
		uintBuf[i] ^= mod;
	}

	uintBuf[8] ^= uintBuf[14];
	uintBuf[6] ^= uintBuf[8];
	uintBuf[12] ^= uintBuf[6];
	uintBuf[0] ^= uintBuf[12];
	uintBuf[11] ^= uintBuf[0];
	uintBuf[13] ^= uintBuf[11];
	uintBuf[1] ^= uintBuf[13];
	uintBuf[10] ^= uintBuf[1];
	uintBuf[9] ^= uintBuf[10];
	uintBuf[3] ^= uintBuf[9];
	uintBuf[7] ^= uintBuf[3];
	uintBuf[4] ^= uintBuf[7];
	uintBuf[2] ^= uintBuf[4];
	uintBuf[5] ^= uintBuf[2];
	uintBuf[14] ^= uintBuf[5];
}

static async Task<int> IntroCmd(string[] a)
{
	if (a.Length < 2)
	{
		PrintUsage();
		return 1;
	}

	foreach (var raw in a.Skip(1).Take(16))
	{
		var volumeFile = Path.GetFullPath(raw);
		Console.WriteLine($"=== {Path.GetFileName(volumeFile)} ===");
		var reader = new NefsReader(new System.IO.Abstractions.FileSystem());
		var arch = await reader.ReadArchiveAsync(volumeFile, new NefsProgress());
		var h = (NefsHeader151)arch.Header;
		var intro = h.Intro;

		Console.WriteLine($"  magic=0x{intro.Magic:X8} tocSize={intro.TocSize}");
		Console.WriteLine($"  version={intro.Version} volumes={intro.NumVolumes} entries={intro.NumEntries}");
		Console.WriteLine($"  blockSize={intro.BlockSize} splitSize={intro.SplitSize}");
		Console.WriteLine($"  entryStart={intro.EntryTableStart} sharedStart={intro.SharedEntryInfoTableStart} nameStart={intro.NameTableStart}");
		Console.WriteLine($"  blockStart={intro.BlockTableStart} volumeInfoStart={intro.VolumeInfoTableStart}");
		Console.WriteLine($"  userValue={intro.UserValue:X8} randomPadding1={intro.RandomPadding1:X8}");
		Console.WriteLine($"  checksum=0x{intro.Checksum:X8} aesKey={intro.AesKey.ToString()} unused={intro.Unused:X8}");
	}

	return 0;
}

static async Task<int> HeaderDiffCmd(string[] a)
{
	if (a.Length < 3)
	{
		PrintUsage();
		return 1;
	}

	var fa = Path.GetFullPath(a[1]);
	var fb = Path.GetFullPath(a[2]);
	var reader = new NefsReader(new System.IO.Abstractions.FileSystem());
	var archA = await reader.ReadArchiveAsync(fa, new NefsProgress());
	var archB = await reader.ReadArchiveAsync(fb, new NefsProgress());
	var hA = (NefsHeader151)archA.Header;
	var hB = (NefsHeader151)archB.Header;

	Console.WriteLine($"A={Path.GetFileName(fa)} B={Path.GetFileName(fb)}");
	Console.WriteLine($"toc sizes: A={hA.Intro.TocSize} B={hB.Intro.TocSize}");

	// Compare intro (before data offset)
	using var fsA = File.OpenRead(fa);
	using var fsB = File.OpenRead(fb);
	var end = (long)Math.Max(hA.Intro.TocSize, hB.Intro.TocSize);
	var bufA = new byte[1 << 20];
	var bufB = new byte[1 << 20];

	long pos = 0;
	var rows = new List<(string kind, long index, long off, byte[] aBytes, byte[] bBytes)>();
	while (pos < end)
	{
		var n = (int)Math.Min(bufA.Length, end - pos);
		fsA.Position = pos;
		fsB.Position = pos;
		fsA.ReadExactly(bufA, 0, n);
		fsB.ReadExactly(bufB, 0, n);
		for (var i = 0; i < n; i++)
		{
			if (bufA[i] != bufB[i])
			{
				long off = pos + i;
				rows.Add(("?", -1, off, null, null));
			}
		}
		pos += n;
	}

	Console.WriteLine($"TOC byte diffs: {rows.Count}");

	// Entry table rows = 24 bytes, from EntryTableStart
	long entryStart = hA.Intro.EntryTableStart;
	var entryDiff = new List<(long idx, ulong sa, ushort va, ushort fa, uint siA, uint fbA, ulong sb, ushort vb, ushort fbb, uint siB, uint fbB)>();
	for (long i = 0; i < hA.Intro.NumEntries; i++)
	{
		var off = entryStart + i * 24;
		if (off > end) break;
		byte[] ra = new byte[24];
		byte[] rb = new byte[24];
		fsA.Position = off; fsB.Position = off;
		fsA.ReadExactly(ra, 0, 24);
		fsB.ReadExactly(rb, 0, 24);
		if (!ra.SequenceEqual(rb))
		{
			entryDiff.Add((i,
				BinaryPrimitives.ReadUInt64BigEndian(ra.AsSpan(0, 8)), BinaryPrimitives.ReadUInt16BigEndian(ra.AsSpan(8, 2)),
				BinaryPrimitives.ReadUInt16BigEndian(ra.AsSpan(10, 2)), BinaryPrimitives.ReadUInt32BigEndian(ra.AsSpan(12, 4)),
				BinaryPrimitives.ReadUInt32BigEndian(ra.AsSpan(16, 4)),
				BinaryPrimitives.ReadUInt64BigEndian(rb.AsSpan(0, 8)), BinaryPrimitives.ReadUInt16BigEndian(rb.AsSpan(8, 2)),
				BinaryPrimitives.ReadUInt16BigEndian(rb.AsSpan(10, 2)), BinaryPrimitives.ReadUInt32BigEndian(rb.AsSpan(12, 4)),
				BinaryPrimitives.ReadUInt32BigEndian(rb.AsSpan(16, 4))));
		}
	}

	Console.WriteLine($"ENTRY TABLE diffs: {entryDiff.Count}");
	foreach (var d in entryDiff.Take(40))
	{
		var nameA = "-";
		var nameB = "-";
		try { nameA = archA.Items.GetItemFilePath(new NefsItemId((int)d.idx)); } catch { }
		try { nameB = archB.Items.GetItemFilePath(new NefsItemId((int)d.idx)); } catch { }
		Console.WriteLine(
			$"  id={d.idx} [{nameA}|{nameB}] start {d.sa}->{d.sb} vol {d.va}->{d.vb} flags {d.fa}->{d.fbb} " +
			$"shared {d.siA}->{d.siB} firstBlock {d.fbA}->{d.fbB}");
	}

	// Shared entry info table rows = 28 bytes
	long sharedStart = hA.Intro.SharedEntryInfoTableStart;
	var sharedDiff = 0;
	for (long i = 0; i < 5000; i++)
	{
		var off = sharedStart + i * 28;
		if (off + 28 > end) break;
		byte[] ra = new byte[28];
		byte[] rb = new byte[28];
		fsA.Position = off; fsB.Position = off;
		fsA.ReadExactly(ra, 0, 28);
		fsB.ReadExactly(rb, 0, 28);
		if (!ra.SequenceEqual(rb))
		{
			Console.WriteLine($"  shared[{i}] A={Convert.ToHexString(ra)} B={Convert.ToHexString(rb)}");
			sharedDiff++;
		}
	}
	Console.WriteLine($"SHARED INFO TABLE (first 5000 rows) diffs: {sharedDiff}");

	// Name table
	long nameStart = hA.Intro.NameTableStart;
	long nameEndA = hA.Intro.BlockTableStart;
	long nameEndB = hB.Intro.BlockTableStart;
	long nameMax = Math.Max(nameEndA, nameEndB);
	var nameBytes = 0;
	fsA.Position = nameStart; fsB.Position = nameStart;
	byte[] na = new byte[(int)(nameMax - nameStart)];
	byte[] nb = new byte[(int)(nameMax - nameStart)];
	fsA.ReadExactly(na, 0, na.Length);
	fsB.ReadExactly(nb, 0, nb.Length);
	for (var i = 0; i < na.Length; i++) if (na[i] != nb[i]) nameBytes++;
	Console.WriteLine($"NAME TABLE (to blockStart) diffs: {nameBytes} bytes");

	// Block table rows = 8 bytes
	long blockStart = hA.Intro.BlockTableStart;
	long blockEndA = hA.Intro.VolumeInfoTableStart;
	long blockEndB = hB.Intro.VolumeInfoTableStart;
	var blockDiff = 0;
	long maxBlocks = Math.Max(blockEndA, blockEndB) - blockStart;
	for (long i = 0; i < maxBlocks; i++)
	{
		var off = blockStart + i * 8;
		if (off + 8 > end) break;
		byte[] ra = new byte[8];
		byte[] rb = new byte[8];
		fsA.Position = off; fsB.Position = off;
		fsA.ReadExactly(ra, 0, 8);
		fsB.ReadExactly(rb, 0, 8);
		if (!ra.SequenceEqual(rb))
		{
			Console.WriteLine($"  block[{i}] A={Convert.ToHexString(ra)} B={Convert.ToHexString(rb)}");
			blockDiff++;
		}
	}
	Console.WriteLine($"BLOCK TABLE (to volumeInfoStart) diffs: {blockDiff}");

	// Volume info
	long viStart = hA.Intro.VolumeInfoTableStart;
	long viEnd = Math.Max(hA.Intro.TocSize, hB.Intro.TocSize);
	var viBytes = 0;
	fsA.Position = viStart; fsB.Position = viStart;
	byte[] va2 = new byte[(int)(viEnd - viStart)];
	byte[] vb2 = new byte[(int)(viEnd - viStart)];
	fsA.ReadExactly(va2, 0, va2.Length);
	fsB.ReadExactly(vb2, 0, vb2.Length);
	for (var i = 0; i < va2.Length; i++) if (va2[i] != vb2[i])
	{
		Console.WriteLine($"  volinfo byte {i} (abs 0x{viStart + i:X}): A={va2[i]:X2} B={vb2[i]:X2}");
		viBytes++;
	}
	Console.WriteLine($"VOLUME INFO TABLE (to tocSize) diffs: {viBytes} bytes");

	return 0;
}

static async Task<int> FlipCmd(string[] a)
{
	if (a.Length < 4)
	{
		PrintUsage();
		return 1;
	}

	var volumeFile = Path.GetFullPath(a[1]);
	var offset = Convert.ToInt64(a[2], 16);
	var newByte = Convert.ToByte(a[3], 16);

	using var fs = new FileStream(volumeFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
	fs.Position = offset;
	var old = fs.ReadByte();
	Console.WriteLine($"[{Path.GetFileName(volumeFile)}] offset 0x{offset:X} {old:X2} -> {newByte:X2}");
	fs.Position = offset;
	fs.WriteByte(newByte);
	fs.Flush(true);
	return 0;
}

static async Task<int> IntrotweakCmd(string[] a)
{
	if (a.Length < 4)
	{
		PrintUsage();
		return 1;
	}

	var volumeFile = Path.GetFullPath(a[1]);
	var idx = Convert.ToInt32(a[2], 16);
	var newValue = Convert.ToUInt32(a[3], 16);
	if (idx < 0 || idx > 31)
	{
		Console.WriteLine("dwordIdx must be 0..1F");
		return 1;
	}

	var rawIntro = new byte[128];
	using (var rs = File.OpenRead(volumeFile))
	{
		rs.ReadExactly(rawIntro, 0, 128);
	}
	var (oldValue, encoded) = TweakIntroWord(rawIntro, idx, newValue);

	using (var fs = new FileStream(volumeFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
	{
		fs.Position = 0;
		fs.Write(encoded, 0, 128);
		fs.Flush(true);
	}
	Console.WriteLine($"[{Path.GetFileName(volumeFile)}] intro dword[{idx:X2}] 0x{oldValue:X8} -> 0x{newValue:X8}");
	return 0;
}

static (uint OldValue, byte[] Encoded) TweakIntroWord(byte[] rawIntro, int idx, uint newValue)
{
	var words = new uint[32];
	var span = new Span<uint>(words);
	DecodeXorIntro(rawIntro, span);
	var oldValue = words[idx];
	words[idx] = newValue;

	var encoded = new byte[128];
	MemoryMarshal.AsBytes(new Span<uint>(words)).CopyTo(encoded);
	EncodeXorIntro(encoded);
	return (oldValue, encoded);
}

static async Task<int> CrackCmd(string[] a)
{
	if (a.Length < 2)
	{
		PrintUsage();
		return 1;
	}

	var volumeFile = Path.GetFullPath(a[1]);
	var reader = new NefsReader(new System.IO.Abstractions.FileSystem());
	var arch = await reader.ReadArchiveAsync(volumeFile, new NefsProgress());
	var h = (NefsHeader151)arch.Header;
	uint target = h.Intro.Checksum;
	Console.WriteLine($"target checksum (decoded idx14) = 0x{target:X8}");

	if (h.Intro.TocSize > int.MaxValue)
	{
		Console.WriteLine("TocSize too large.");
		return 1;
	}

	var tocLen = (int)h.Intro.TocSize;
	var toc = new byte[tocLen];
	using (var fs = File.OpenRead(volumeFile))
	{
		fs.ReadExactly(toc, 0, tocLen);
	}

	// Candidate windows within the TOC (absolute offsets into the file/header data).
	var windows = new List<(string Name, int Start, int End)>
	{
		("whole-toc", 0, tocLen),
		("after-intro", 128, tocLen),
		("entry-only", (int)h.Intro.EntryTableStart, (int)h.Intro.SharedEntryInfoTableStart),
		("entry+shared", (int)h.Intro.EntryTableStart, (int)h.Intro.NameTableStart),
		("tables-subtoc", (int)h.Intro.EntryTableStart, (int)h.Intro.VolumeInfoTableStart),
		("toc-minus-checksum-zeroed", 0, tocLen),
		("cheader[0,data)", 0, (int)h.Intro.EntryTableStart),
		("balHeader-5", 0, (int)h.Intro.BlockTableStart),
		("balHeader-6", 0, (int)h.Intro.VolumeInfoTableStart),
		("intro[0,128)", 0, 128),
		("intro[0,60)csz", 0, 60),
		("intro[0,56)", 0, 56),
		("name-only", (int)h.Intro.NameTableStart, (int)h.Intro.BlockTableStart),
		("block-only", (int)h.Intro.BlockTableStart, (int)h.Intro.VolumeInfoTableStart),
		("volinfo-only", (int)h.Intro.VolumeInfoTableStart, tocLen),
	};

	foreach (var w in windows)
	{
		if (w.Start < 0 || w.End > tocLen || w.End <= w.Start) continue;
		if (w.Name == "toc-minus-checksum-zeroed") continue; // handled below
		CrackWindow(toc, w.Name, w.Start, w.End, target);
	}

	// Whole TOC with the checksum field (offset 0x38, 4 bytes) zeroed.
	var zeroed = (byte[])toc.Clone();
	Array.Clear(zeroed, 0x38, 4);
	CrackWindow(zeroed, "toc-checksum-zeroed", 0, tocLen, target);
	CrackWindow(zeroed, "intro-csz", 0, 128, target);
	CrackWindow(zeroed, "after-intro-csz", 128, tocLen, target);

	// Decoded intro windows. The game decodes the XOR intro before inspecting it,
	// so a hash over the decoded header is a strong candidate.
	var (decodedIntro, words) = MakeDecodedIntro(toc);

	CrackWindow(decodedIntro, "dec-intro[0,128)", 0, 128, target);
	CrackWindow(decodedIntro, "dec-intro[0,128)csz", 0, 128, target);

	// decoded intro + raw tables (game would hash the header structures it built in memory)
	var decPlusTables = new byte[tocLen];
	Array.Copy(decodedIntro, 0, decPlusTables, 0, 128);
	Array.Copy(toc, 128, decPlusTables, 128, tocLen - 128);
	Array.Clear(decPlusTables, 0x38, 4);
	CrackWindow(decPlusTables, "dec-intro+tables-csz", 0, tocLen, target);

	// word-swapped decoded intro (fields may be serialized big-endian for hashing)
	var beWords = new byte[128];
	for (var i = 0; i < 32; i++)
	{
		BinaryPrimitives.WriteUInt32BigEndian(beWords.AsSpan(i * 4, 4), words[i]);
	}
	CrackWindow(beWords, "dec-intro-be", 0, 128, target);
	var bePlusTables = new byte[tocLen];
	Array.Copy(beWords, 0, bePlusTables, 0, 128);
	Array.Copy(toc, 128, bePlusTables, 128, tocLen - 128);
	Array.Clear(bePlusTables, 0x38, 4);
	CrackWindow(bePlusTables, "dec-intro-be+tables-csz", 0, tocLen, target);

	// raw intro + tables (i.e. the encoded intro as stored, then tables)
	var rawPlusTables = new byte[tocLen];
	Array.Copy(toc, 0, rawPlusTables, 0, tocLen);
	Array.Clear(rawPlusTables, 0x38, 4);
	CrackWindow(rawPlusTables, "raw-intro+tables-csz", 0, tocLen, target);

	return 1;
}

static async Task<int> SweepCmd(string[] a)
{
	if (a.Length < 2)
	{
		PrintUsage();
		return 1;
	}

	var volumeFile = Path.GetFullPath(a[1]);
	var reader = new NefsReader(new System.IO.Abstractions.FileSystem());
	var arch = await reader.ReadArchiveAsync(volumeFile, new NefsProgress());
	var h = (NefsHeader151)arch.Header;
	uint target = h.Intro.Checksum;
	Console.WriteLine($"target checksum (decoded idx14) = 0x{target:X8}");

	var tocLen = (int)h.Intro.TocSize;
	var toc = new byte[tocLen];
	using (var fs = File.OpenRead(volumeFile))
	{
		fs.ReadExactly(toc, 0, tocLen);
	}

	var (decodedIntro, words) = MakeDecodedIntro(toc);

	// Build candidate byte segments. The checksum field is decoded word 14 which
	// occupies bytes 0x38..0x3B in both the raw and decoded intro.
	List<(string Name, byte[] Data)> segments = new();

	// Raw TOC variants.
	segments.Add(("raw-toc", toc));
	segments.Add(("raw-toc-skip", SkipRange(toc, 0x38, 0x3C)));          // [0,0x38)+[0x3C,end)
	segments.Add(("raw-toc-zeroed", ZeroRange(toc, 0x38, 0x3C)));

	// Decoded intro only.
	segments.Add(("dec-intro", decodedIntro));
	segments.Add(("dec-intro-skip", SkipRange(decodedIntro, 0x38, 0x3C)));
	segments.Add(("dec-intro-zeroed", ZeroRange(decodedIntro, 0x38, 0x3C)));
	segments.Add(("dec-intro-words[0,14)", SliceWords(decodedIntro, 0, 14)));
	segments.Add(("dec-intro-words[0,14)skip14", Concat(decodedIntro.AsSpan(0, 56).ToArray(), decodedIntro.AsSpan(60, 68).ToArray())));

	// Decoded intro + raw tables (mimics the v1.6 hash pattern over the in-memory header).
	var decPlusTables = Concat(decodedIntro, toc.AsSpan(128, tocLen - 128).ToArray());
	segments.Add(("dec-tables", decPlusTables));
	segments.Add(("dec-tables-skip", SkipRange(decPlusTables, 0x38, 0x3C)));
	segments.Add(("dec-tables-zeroed", ZeroRange(decPlusTables, 0x38, 0x3C)));

	// Raw intro + raw tables.
	var rawPlusTables = Concat(toc.AsSpan(0, 128).ToArray(), toc.AsSpan(128, tocLen - 128).ToArray());
	segments.Add(("raw-tables", rawPlusTables));
	segments.Add(("raw-tables-skip", SkipRange(rawPlusTables, 0x38, 0x3C)));
	segments.Add(("raw-tables-zeroed", ZeroRange(rawPlusTables, 0x38, 0x3C)));

	// BE word-swapped decoded intro + tables (game may serialize BE).
	var beIntro = new byte[128];
	for (var i = 0; i < 32; i++) BinaryPrimitives.WriteUInt32BigEndian(beIntro.AsSpan(i * 4, 4), words[i]);
	var bePlusTables = Concat(beIntro, toc.AsSpan(128, tocLen - 128).ToArray());
	segments.Add(("be-tables", bePlusTables));
	segments.Add(("be-tables-skip", SkipRange(bePlusTables, 0x38, 0x3C)));
	segments.Add(("be-tables-zeroed", ZeroRange(bePlusTables, 0x38, 0x3C)));

	// Entry table only (this is what AV.nfs changed).
	var entryOnly = toc.AsSpan((int)h.Intro.EntryTableStart, (int)(h.Intro.SharedEntryInfoTableStart - h.Intro.EntryTableStart)).ToArray();
	segments.Add(("entry-only", entryOnly));

	// All tables from the end of the intro to the end of the TOC (T12 proved intro words 0-13 aren't covered).
	var tablesAll = toc.AsSpan(128, tocLen - 128).ToArray();
	segments.Add(("tables[128,end)", tablesAll));

	// tables minus the volume info table
	var tablesNoVol = toc.AsSpan(128, (int)h.Intro.VolumeInfoTableStart - 128).ToArray();
	segments.Add(("tables[128,volinfo)", tablesNoVol));

	// tables minus block and volume info (entry+shared+name)
	var tablesNoBlock = toc.AsSpan(128, (int)h.Intro.BlockTableStart - 128).ToArray();
	segments.Add(("tables[128,block)", tablesNoBlock));

	// Block table only.
	var blockOnly = toc.AsSpan((int)h.Intro.BlockTableStart, (int)(h.Intro.VolumeInfoTableStart - h.Intro.BlockTableStart)).ToArray();
	segments.Add(("block-only", blockOnly));

	// name+block+volinfo (everything except intro and entry)
	var rest = toc.AsSpan((int)h.Intro.NameTableStart, tocLen - (int)h.Intro.NameTableStart).ToArray();
	segments.Add(("name-block-volinfo", rest));

	// Field-aware big-endian serialization of each table (game's native in-memory layout
	// on Xenon PPC, big-endian). These havehes the bytes the game would hold in memory.
	var beEntry = BuildBeTable(toc, "entry", h);
	var beShared = BuildBeTable(toc, "shared", h);
	var beName = BuildBeTable(toc, "name", h);
	var beBlock = BuildBeTable(toc, "block", h);
	var beVol = BuildBeTable(toc, "volinfo", h);
	segments.Add(("be-entry", beEntry));
	segments.Add(("be-shared", beShared));
	segments.Add(("be-name", beName));
	segments.Add(("be-block", beBlock));
	segments.Add(("be-vol", beVol));
	segments.Add(("be-all", Concat(Concat(Concat(Concat(beEntry, beShared), beName), beBlock), beVol)));
	segments.Add(("be-espn", Concat(Concat(Concat(beEntry, beShared), beName), beBlock))); // entry+shared+name+block
	segments.Add(("be-es", Concat(beEntry, beShared)));
	segments.Add(("be-blockNameExcl? blocktail", beBlock));

	// BE entry table + LE remaining (mixed)
	segments.Add(("be-entry+rest", Concat(beEntry, toc.AsSpan((int)h.Intro.SharedEntryInfoTableStart, tocLen - (int)h.Intro.SharedEntryInfoTableStart).ToArray())));

	Console.WriteLine($"testing {segments.Count} segments...");

	var found = false;
	foreach (var seg in segments)
	{
		if (SweepSegment(seg.Name, seg.Data, target)) found = true;
	}
	if (!found) Console.WriteLine("no match.");
	return found ? 0 : 1;
}

static async Task<int> TablesCmd(string[] a)
{
	if (a.Length < 2) { PrintUsage(); return 1; }
	var volumeFile = Path.GetFullPath(a[1]);
	var arch = await Open(volumeFile);
	var h = (NefsHeader151)arch.Header;
	Console.WriteLine($"entryTable count={h.EntryTable.Entries.Count}");
	Console.WriteLine($"sharedInfo count={h.SharedEntryInfoTable.Entries.Count}");
	Console.WriteLine($"blockTable count={h.BlockTable.Entries.Count}");
	Console.WriteLine($"volInfo count={h.VolumeInfoTable.Entries.Count}");
	foreach (var v in h.Volumes) Console.WriteLine($"  size=0x{v.Size:X} dataOff=0x{v.DataOffset:X} name={v.Name}");
	Console.WriteLine("first entries (idx, Start, Volume, Flags, SharedInfo, FirstBlock, NextDuplicate):");
	for (int i = 0; i < Math.Min(10, h.EntryTable.Entries.Count); i++)
	{
		var e = h.EntryTable.Entries[i];
		Console.WriteLine($"  {i}: start=0x{e.Start:X} vol={e.Volume} flags=0x{e.Flags:X4} sharedInfo=0x{e.SharedInfo:X8} firstBlock={e.FirstBlock} nextDup={e.NextDuplicate}");
	}
	Console.WriteLine("last 3 entries:");
	for (int i = Math.Max(0, h.EntryTable.Entries.Count - 3); i < h.EntryTable.Entries.Count; i++)
	{
		var e = h.EntryTable.Entries[i];
		Console.WriteLine($"  {i}: start=0x{e.Start:X} vol={e.Volume} flags=0x{e.Flags:X4} sharedInfo=0x{e.SharedInfo:X8} firstBlock={e.FirstBlock} nextDup={e.NextDuplicate}");
	}
	Console.WriteLine("shared entries 0..3 and last 2 (ParentOffset, NextSiblingOffset, FirstChildOffset, NameOffset, Size, FirstDuplicatedItemOffset, PatchedEntry):");
	foreach (var idx in new[] { 0, 1, 2, 3, h.SharedEntryInfoTable.Entries.Count - 2, h.SharedEntryInfoTable.Entries.Count - 1 })
	{
		if (idx < 0 || idx >= h.SharedEntryInfoTable.Entries.Count) continue;
		var s = h.SharedEntryInfoTable.Entries[idx];
		Console.WriteLine($"  s{idx}: parent=0x{s.Parent:X} nextSib=0x{s.NextSibling:X} firstChild=0x{s.FirstChild:X} nameOff=0x{s.NameOffset:X} size=0x{s.Size:X} firstDup=0x{s.FirstDuplicate:X} patched=0x{s.PatchedEntry:X8}");
	}
	Console.WriteLine("==== DATA PASS END ====");
	return 0;
}

static async Task<int> BlockSumCmd(string[] a)
{
	if (a.Length < 2) { PrintUsage(); return 1; }
	var volumeFile = Path.GetFullPath(a[1]);
	var reader = new NefsReader(new System.IO.Abstractions.FileSystem());
	var arch = await reader.ReadArchiveAsync(volumeFile, new NefsProgress());
	var h = (NefsHeader151)arch.Header;
	var tocLen = (int)h.Intro.TocSize;
	var toc = new byte[tocLen];
	using (var fs0 = File.OpenRead(volumeFile)) fs0.ReadExactly(toc, 0, tocLen);

	var blockStart = (int)h.Intro.BlockTableStart;
	var volInfoStart = (int)h.Intro.VolumeInfoTableStart;
	var recCount = (volInfoStart - blockStart) / 8;
	Console.WriteLine($"dbg blockStart={blockStart} volInfoStart={volInfoStart} recCount={recCount}");

	// Data starts where volume 0's data begins (aligned after the TOC).
	var dataOff = (long)h.Volumes[0].DataOffset;
	if (dataOff == 0) dataOff = (tocLen + 0xFF) & ~0xFFL;
	Console.WriteLine($"blocks={recCount} dataOffset=0x{dataOff:X}");

	var table = BuildCrc32Table();
	uint Fold(uint crc) => (ushort)((ushort)crc + (crc >> 16));

	using var fs = File.OpenRead(volumeFile);
	var buf = new byte[4 * 1024 * 1024];
	long streamPos = dataOff;
	int ok = 0, bad = 0;
	var prevEnd = 0u;
	var firstBad = new List<string>();
	for (var i = 0; i < recCount; i++)
	{
		var end = BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(blockStart + i * 8, 4));
		var stored = BinaryPrimitives.ReadUInt16LittleEndian(toc.AsSpan(blockStart + i * 8 + 6, 2));
		if (i < 8) Console.WriteLine($"dbg rec{i}: End=0x{end:X8} F=0x{BinaryPrimitives.ReadUInt16LittleEndian(toc.AsSpan(blockStart + i * 8 + 4, 2)):X4} C=0x{stored:X4}");
		var start = dataOff + prevEnd;
		var len = (long)(end - prevEnd);
		uint crc = 0xFFFFFFFFu;
		long remain = len;
		while (remain > 0)
		{
			var toRead = (int)Math.Min(buf.Length, remain);
			if (streamPos + toRead > fs.Length) { remain = 0; break; }
			fs.Position = streamPos;
			var n = fs.Read(buf, 0, toRead);
			if (n <= 0) break;
			for (var k = 0; k < n; k++) crc = (crc >> 8) ^ table[(byte)(crc ^ buf[k])];
			streamPos += n;
			remain -= n;
		}
		var computed = Fold(crc ^ 0xFFFFFFFFu);
		if (computed == stored) ok++;
		else
		{
			bad++;
			if (firstBad.Count < 10) firstBad.Add($"  block#{i} off=0x{start:X} len={len} stored=0x{stored:X4} computed=0x{computed:X4}");
		}
		prevEnd = end;
	}
	Console.WriteLine($"crc16 matches={ok} mismatches={bad}");
	if (firstBad.Count > 0) Console.WriteLine(string.Join("\n", firstBad));
	return 0;
}

static async Task<int> DataCrcCmd(string[] a)
{
	if (a.Length < 2)
	{
		PrintUsage();
		return 1;
	}

	var volumeFile = Path.GetFullPath(a[1]);
	var reader = new NefsReader(new System.IO.Abstractions.FileSystem());
	var arch = await reader.ReadArchiveAsync(volumeFile, new NefsProgress());
	var h = (NefsHeader151)arch.Header;
	uint target = h.Intro.Checksum;
	var tocLen = (long)h.Intro.TocSize;
	Console.WriteLine($"target checksum = 0x{target:X8}");
	Console.WriteLine($"tocLen = {tocLen} (0x{tocLen:X})");

	var fs = File.OpenRead(volumeFile);
	var fileLen = fs.Length;
	var dataOff = (tocLen + 0xFF) & ~0xFFL; // data begins after aligned toc

	// Read whole TOC (header + tables) for table-level invariants.
	var tocRaw = new byte[tocLen];
	fs.Position = 0;
	fs.ReadExactly(tocRaw, 0, (int)tocLen);

	var blockStart = (int)h.Intro.BlockTableStart;
	var volInfoStart = (int)h.Intro.VolumeInfoTableStart;
	var recCount = (volInfoStart - blockStart) / 8;
	var blockCrc16Sum = 0u;
	var blockCrc16Xor = 0u;
	var blockCrc16Crc32 = 0xFFFFFFFFu;
	for (var i = 0; i < recCount; i++)
	{
		var off = blockStart + i * 8 + 6; // checksum lives in the last 2 bytes of the 8-byte record
		var v16 = BinaryPrimitives.ReadUInt16LittleEndian(tocRaw.AsSpan(off, 2));
		blockCrc16Sum = (blockCrc16Sum + v16) & 0xFFFFFFFFu;
		blockCrc16Xor ^= v16;
	}
	Console.WriteLine($"block records={recCount}  sumCrc16=0x{blockCrc16Sum:X8}  xorCrc16=0x{blockCrc16Xor:X8}"
		+ (blockCrc16Sum == target ? "   *** MATCH sumCrc16" : "")
		+ (blockCrc16Xor == target ? "   *** MATCH xorCrc16" : ""));

	// Sum of ALL table u16 fields as the game might accumulate, plus fold.
	var u16Sum0 = 0u;
	var u16Xor0 = 0u;
	for (var i = 128; i + 2 <= volInfoStart; i += 2)
	{
		var v = BinaryPrimitives.ReadUInt16LittleEndian(tocRaw.AsSpan(i, 2));
		u16Sum0 = (u16Sum0 + v) & 0xFFFFFFFFu;
		u16Xor0 ^= v;
	}
	Console.WriteLine($"u16sum[128,volinfo)=0x{u16Sum0:X8}  u16xor[128,volinfo)=0x{u16Xor0:X8}"
		+ (u16Sum0 == target ? "   *** MATCH" : "") + (u16Xor0 == target ? "   *** MATCH" : ""));

	var table = BuildCrc32Table();
	blockCrc16Crc32 = 0xFFFFFFFFu;
	for (var i = 0; i < recCount; i++)
	{
		var off = blockStart + i * 8 + 6;
		var v16 = BinaryPrimitives.ReadUInt16LittleEndian(tocRaw.AsSpan(off, 2));
		blockCrc16Crc32 = (blockCrc16Crc32 >> 8) ^ table[(byte)(blockCrc16Crc32 ^ (v16 & 0xFF))];
		blockCrc16Crc32 = (blockCrc16Crc32 >> 8) ^ table[(byte)(blockCrc16Crc32 ^ (v16 >> 8))];
	}
	var blockCrc16Crc32Std = blockCrc16Crc32 ^ 0xFFFFFFFFu;
	Console.WriteLine($"crc32-of-crc16-table=0x{blockCrc16Crc32Std:X8}"
		+ (blockCrc16Crc32Std == target ? "   *** MATCH" : ""));

	(long Start, long End, string Name)[] ranges =
	{
		(dataOff, fileLen, $"data[{dataOff},EOF)"),
		(tocLen, fileLen, $"data[{tocLen},EOF)"),
		(128, fileLen, "[128,EOF)"),
		(0, fileLen, "whole-file"),
		((long)h.Intro.VolumeInfoTableStart, fileLen, $"volinfo[{h.Intro.VolumeInfoTableStart},EOF)"),
	};

	foreach (var r in ranges)
	{
		if (r.Start >= r.End) { Console.WriteLine($"skip {r.Name}: start>=end"); continue; }

		// standard CRC32 (init 0xFFFFFFFF, xorout 0xFFFFFFFF)
		uint crc = 0xFFFFFFFFu;
		// CRC32 with init 0, no final xor (NefsLib ComputeCrc32(0,x) inner state)
		uint crc0 = 0u;
		// one's-complement 16-bit sum of data bytes
		uint osum = 0u;
		// stream hashes
		using var md5 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
		using var sha1 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA1);
		using var sha256 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);

		var buf = new byte[4 * 1024 * 1024];
		var remaining = r.End - r.Start;
		fs.Position = r.Start;
		ulong total = 0;
		while (remaining > 0)
		{
			var toRead = (int)Math.Min(buf.Length, remaining);
			var n = fs.Read(buf, 0, toRead);
			if (n <= 0) break;
			int effStart = 0, effEnd = n;
			// skip the 4-byte checksum field if this range spans it (whole-file / [0,EOF))
			if (r.Start == 0 && r.End == fileLen)
			{
				if (effStart < 0x3C && effEnd > 0x38)
				{
					md5.AppendData(buf, effStart, 0x38 - effStart);
					sha1.AppendData(buf, effStart, 0x38 - effStart);
					sha256.AppendData(buf, effStart, 0x38 - effStart);
					if (effEnd > 0x3C) { md5.AppendData(buf, 0x3C, effEnd - 0x3C); sha1.AppendData(buf, 0x3C, effEnd - 0x3C); sha256.AppendData(buf, 0x3C, effEnd - 0x3C); }
					effStart = -1;
				}
			}
			else if (effStart >= 0)
			{
				md5.AppendData(buf, effStart, effEnd - effStart);
				sha1.AppendData(buf, effStart, effEnd - effStart);
				sha256.AppendData(buf, effStart, effEnd - effStart);
			}
			for (var i = 0; i < n; i++)
			{
				var b = buf[i];
				crc = (crc >> 8) ^ table[(byte)(crc ^ b)];
				crc0 = (crc0 >> 8) ^ table[(byte)(crc0 ^ b)];
				osum += b;
			}
			total += (ulong)n;
			remaining -= n;
		}
		var std = crc ^ 0xFFFFFFFFu;
		uint osumFolded = osum;
		while ((osumFolded >> 16) != 0) osumFolded = (osumFolded & 0xFFFF) + (osumFolded >> 16);

		var md5b = md5.GetHashAndReset();
		var sha1b = sha1.GetHashAndReset();
		var sha256b = sha256.GetHashAndReset();
		uint md5Lo = BinaryPrimitives.ReadUInt32LittleEndian(md5b.AsSpan(0, 4));
		uint md5Hi = BinaryPrimitives.ReadUInt32LittleEndian(md5b.AsSpan(md5b.Length - 4, 4));
		uint sha1Lo = BinaryPrimitives.ReadUInt32LittleEndian(sha1b.AsSpan(0, 4));
		uint sha1Hi = BinaryPrimitives.ReadUInt32LittleEndian(sha1b.AsSpan(sha1b.Length - 4, 4));
		uint sha256Lo = BinaryPrimitives.ReadUInt32LittleEndian(sha256b.AsSpan(0, 4));
		uint sha256Hi = BinaryPrimitives.ReadUInt32LittleEndian(sha256b.AsSpan(sha256b.Length - 4, 4));

		Console.WriteLine($"{r.Name}: len={total}  crc32std=0x{std:X8}  crc32init0=0x{crc0:X8}  onescomp16=0x{osumFolded:X4}"
			+ (r.Start == 0 && r.End == fileLen ? " [checksum-field SKIPPED]" : "")
			+ (std == target ? "   *** MATCH crc32std" : "")
			+ (crc0 == target ? "   *** MATCH crc32init0" : ""));
		Console.WriteLine($"   md5lo=0x{md5Lo:X8} md5hi=0x{md5Hi:X8} sha1lo=0x{sha1Lo:X8} sha1hi=0x{sha1Hi:X8} sha256lo=0x{sha256Lo:X8} sha256hi=0x{sha256Hi:X8}"
			+ (md5Lo == target || md5Hi == target || sha1Lo == target || sha1Hi == target || sha256Lo == target || sha256Hi == target ? "   *** MATCH hash" : ""));
	}
	// Dedicated pass: 128KB-block aggregates over data region [dataStart..dataEnd)
	var dataStart = (long)h.Volumes[0].DataOffset;
	var dataEnd = Math.Min((long)h.Volumes[0].DataOffset + (long)h.Volumes[0].Size, fileLen);
	Console.WriteLine($"data region: [{dataStart:X}..{dataEnd:X}) len={dataEnd - dataStart:X}");
	var blkBuf = new byte[4 * 1024 * 1024];
	var BLK = 0x20000;
	uint sumFold16 = 0, xorFold16 = 0, sumCrc32 = 0, xorCrc32 = 0, chained = 0xFFFFFFFFu;
	for (long s = dataStart; s < dataEnd; s += BLK)
	{
		var e = Math.Min(s + BLK, dataEnd);
		uint crc = 0xFFFFFFFFu;
		fs.Position = s;
		var remain = e - s;
		while (remain > 0)
		{
			var n = fs.Read(blkBuf, 0, (int)Math.Min(blkBuf.Length, remain));
			if (n <= 0) break;
			for (var i = 0; i < n; i++) crc = (crc >> 8) ^ table[(byte)(crc ^ blkBuf[i])];
			remain -= n;
		}
		var std = crc ^ 0xFFFFFFFFu;
		ushort f16 = (ushort)(((std & 0xFFFF) ^ (std >> 16)) & 0xFFFF);
		sumFold16 = (sumFold16 + f16) & 0xFFFF;
		xorFold16 ^= f16;
		sumCrc32 = sumCrc32 + std;
		xorCrc32 ^= std;
		for (var i = 0; i < 4; i++) chained = (chained >> 8) ^ table[(byte)(chained ^ (byte)(std >> (8 * i)))];
	}
	Console.WriteLine($"  sumFold16={sumFold16:X4} xorFold16={xorFold16:X4} sumCrc32={sumCrc32:X8} xorCrc32={xorCrc32:X8} chained={chained ^ 0xFFFFFFFFu:X8}"
		+ (sumFold16 == target ? "   *** MATCH sumFold16" : "") + (xorFold16 == target ? "   *** MATCH xorFold16" : "")
		+ (sumCrc32 == target ? "   *** MATCH sumCrc32" : "") + (xorCrc32 == target ? "   *** MATCH xorCrc32" : "")
		+ ((chained ^ 0xFFFFFFFFu) == target ? "   *** MATCH chained" : ""));
	fs.Dispose();
	return 0;
}

static byte[] Concat(byte[] left, byte[] right)
{
	var outArr = new byte[left.Length + right.Length];
	Buffer.BlockCopy(left, 0, outArr, 0, left.Length);
	Buffer.BlockCopy(right, 0, outArr, left.Length, right.Length);
	return outArr;
}

static byte[] SliceWords(byte[] data, int startWord, int endWord)
{
	var outArr = new byte[(endWord - startWord) * 4];
	Buffer.BlockCopy(data, startWord * 4, outArr, 0, outArr.Length);
	return outArr;
}

static byte[] SkipRange(byte[] data, int skipStart, int skipEnd)
{
	var outArr = new byte[data.Length - (skipEnd - skipStart)];
	Buffer.BlockCopy(data, 0, outArr, 0, skipStart);
	Buffer.BlockCopy(data, skipEnd, outArr, skipStart, data.Length - skipEnd);
	return outArr;
}

static byte[] ZeroRange(byte[] data, int zeroStart, int zeroEnd)
{
	var clone = (byte[])data.Clone();
	Array.Clear(clone, zeroStart, zeroEnd - zeroStart);
	return clone;
}

static byte[] BuildBeTable(byte[] toc, string kind, NefsHeader151 h)
{
	var s = (int)h.Intro.EntryTableStart;
	var sh = (int)h.Intro.SharedEntryInfoTableStart;
	var n = (int)h.Intro.NameTableStart;
	var b = (int)h.Intro.BlockTableStart;
	var vi = (int)h.Intro.VolumeInfoTableStart;

	byte[] Out(int len)
	{
		var arr = new byte[len];
		return arr;
	}

	switch (kind)
	{
		case "entry":
		{
			var cnt = (sh - s) / 24;
			var o = Out(cnt * 24);
			for (var i = 0; i < cnt; i++)
			{
				var src = toc.AsSpan(s + i * 24, 24);
				BinaryPrimitives.WriteUInt64BigEndian(o.AsSpan(i * 24, 8), BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(0, 8)));
				BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(i * 24 + 8, 2), BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(8, 2)));
				BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(i * 24 + 10, 2), BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(10, 2)));
				BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(i * 24 + 12, 4), BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(12, 4)));
				BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(i * 24 + 16, 4), BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(16, 4)));
				BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(i * 24 + 20, 4), BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(20, 4)));
			}
			return o;
		}
		case "shared":
		{
			var cnt = (n - sh) / 28;
			var o = Out(cnt * 28);
			for (var i = 0; i < cnt; i++)
			{
				var src = toc.AsSpan(sh + i * 28, 28);
				for (var f = 0; f < 7; f++)
					BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(i * 28 + f * 4, 4), BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(f * 4, 4)));
			}
			return o;
		}
		case "name":
		{
			var cnt = (b - n) / 4;
			var o = Out(cnt * 4);
			for (var i = 0; i < cnt; i++)
				BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(i * 4, 4), BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(n + i * 4, 4)));
			return o;
		}
		case "block":
		{
			var region = vi - b;
			var cnt = region / 8;
			var o = Out(region);
			for (var i = 0; i < cnt; i++)
			{
				var src = toc.AsSpan(b + i * 8, 8);
				BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(i * 8, 4), BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4)));
				BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(i * 8 + 4, 2), BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(4, 2)));
				BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(i * 8 + 6, 2), BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(6, 2)));
			}
			// trailing padding bytes (region may not be a multiple of 8)
			for (var j = cnt * 8; j < region; j++) o[j] = toc[b + j];
			return o;
		}
		case "volinfo":
		{
			var cnt = (toc.Length - vi) / 16;
			var o = Out(cnt * 16);
			for (var i = 0; i < cnt; i++)
			{
				var src = toc.AsSpan(vi + i * 16, 16);
				BinaryPrimitives.WriteUInt64BigEndian(o.AsSpan(i * 16, 8), BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(0, 8)));
				BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(i * 16 + 8, 4), BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(8, 4)));
				BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(i * 16 + 12, 4), BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(12, 4)));
			}
			return o;
		}
		default:
			return Array.Empty<byte>();
	}
}

static bool SweepSegment(string name, byte[] data, uint target)
{
	var matched = false;

	// --- CRC-32 parameter sweep (reflect-in / reflect-out / init / xorout / poly) ---
	// A broad selection of 32-bit CRC polynomials in normal (non-reflected) form.
	uint[] polys = {
		0x04C11DB7, 0x1EDC6F41, 0x814141AB, 0xA833982B, 0x000000AF, 0x741B8CD7, 0xEB31D82E,
		0xD5828281, 0x8F6E37A0, 0xA4097606, 0x0000009B, 0x8CB351A9, 0x04C0E29B, 0x752B2013,
		0xEDB88320, 0x82F63B78, 0xD4A9E185, 0x6502F24B, 0x9A6372E7, 0x9916CF9D, 0x9C2E1E6D,
		0x5D6DCB20, 0xD3EDC247, 0x3D65CD34, 0xD63B9BA0, 0xB2C8A1CB, 0x10081010, 0xA5AFA6E1,
		0x6B064F5E, 0x272A5BB5, 0xCB99A638, 0xE6DB5DD9, 0x049D5D3E, 0x9A5F0CB5, 0xA797BBBC,
	};
	bool[] reflectCombos = { false, true };
	uint[] inits = { 0x00000000, 0xFFFFFFFF };
	uint[] xorouts = { 0x00000000, 0xFFFFFFFF };

	foreach (var poly in polys)
	{
		if (poly == 0) continue;
		foreach (var refin in reflectCombos)
		{
			foreach (var init in inits)
			{
				foreach (var xorout in xorouts)
				{
					foreach (var refout in reflectCombos)
					{
						var v = Crc32Generic(data, poly, refin, refout, init, xorout);
						if (v == target)
						{
							Console.WriteLine($"  *** MATCH {name} via CRC32 poly=0x{poly:X8} refin={refin} refout={refout} init=0x{init:X8} xorout=0x{xorout:X8} = 0x{v:X8}");
							matched = true;
						}
					}
				}
			}
		}
	}

	// --- additive checksums ---
	var sumLe = Sum32(data, false);
	var sumBe = Sum32(data, true);
	var xorAll = XorAll(data);
	CheckMatch(name, "sum32le", sumLe, target, ref matched);
	CheckMatch(name, "sum32be", sumBe, target, ref matched);
	CheckMatch(name, "xorall", xorAll, target, ref matched);

	// --- Word-sum with end-around carry (Fletcher-style, 1 word), LE and BE ---
	CheckMatch(name, "fletcherw-le", FletcherWord(data, littleEndian: true), target, ref matched);
	CheckMatch(name, "fletcherw-be", FletcherWord(data, littleEndian: false), target, ref matched);

	// --- Fletcher / Jenkins / Murmur / FNV stronger variants ---
	CheckMatch(name, "fletcher32-LE", Fletcher32(data, littleEndian: true), target, ref matched);
	CheckMatch(name, "fletcher32-BE", Fletcher32(data, littleEndian: false), target, ref matched);
	CheckMatch(name, "jenkins-one-at-a-time", JenkinsOat(data), target, ref matched);
	CheckMatch(name, "murmur3-x86-32", Murmur3x8632(data), target, ref matched);
	CheckMatch(name, "fnv1-32", Fnv1(data), target, ref matched);

	// --- DNS/TCP-style ones-complement plus carry sums ---
	CheckMatch(name, "onescomp16", OnesCompSum16(data), target, ref matched);
	CheckMatch(name, "onescomp16wa", OnesCompSum16WordAligned(data), target, ref matched);

	// --- FNV-1a + Adler ---
	CheckMatch(name, "adler32", Adler32(data), target, ref matched);

	// --- SHA-256 / SHA-1 / MD5 truncations ---
	CheckMatch(name, "sha256-loLE", HashTrunc(data, "SHA256", true, false), target, ref matched);
	CheckMatch(name, "sha256-loBE", HashTrunc(data, "SHA256", true, true), target, ref matched);
	CheckMatch(name, "sha256-hiLE", HashTrunc(data, "SHA256", false, false), target, ref matched);
	CheckMatch(name, "sha256-hiBE", HashTrunc(data, "SHA256", false, true), target, ref matched);
	CheckMatch(name, "sha1-loLE", HashTrunc(data, "SHA1", true, false), target, ref matched);
	CheckMatch(name, "sha1-hiBE", HashTrunc(data, "SHA1", false, true), target, ref matched);
	CheckMatch(name, "md5-loLE", HashTrunc(data, "MD5", true, false), target, ref matched);
	CheckMatch(name, "md5-hiBE", HashTrunc(data, "MD5", false, true), target, ref matched);

	return matched;
}

static void CheckMatch(string name, string algo, uint val, uint target, ref bool matched)
{
	if (val == target)
	{
		Console.WriteLine($"  *** MATCH {name} via {algo} = 0x{val:X8}");
		matched = true;
	}
}

static uint OnesCompSum16(ReadOnlySpan<byte> data)
{
	uint sum = 0;
	foreach (var b in data) sum += b;
	while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
	return (~sum) & 0xFFFF;
}

static uint OnesCompSum16WordAligned(ReadOnlySpan<byte> data)
{
	uint sum = 0;
	for (var i = 0; i + 2 <= data.Length; i += 2)
	{
		sum += (uint)(data[i] | ((uint)data[i + 1] << 8));
	}
	if ((data.Length & 1) == 1) sum += data[data.Length - 1];
	while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
	return (~sum) & 0xFFFF;
}

static uint Reflect32(uint v)
{
	var r = 0u;
	for (var i = 0; i < 32; i++)
	{
		if ((v & 1) != 0) r |= 1u << (31 - i);
		v >>= 1;
	}
	return r;
}

static uint Crc32Generic(ReadOnlySpan<byte> data, uint poly, bool refin, bool refout, uint init, uint xorout)
{
	uint crc;
	if (refin)
	{
		var reflectedPoly = Reflect32(poly);
		crc = Reflect32(init);
		foreach (var b in data)
		{
			crc ^= b;
			for (var k = 0; k < 8; k++)
			{
				crc = (crc & 1) != 0 ? (crc >> 1) ^ reflectedPoly : crc >> 1;
			}
		}
	}
	else
	{
		crc = init;
		foreach (var b in data)
		{
			crc ^= (uint)b << 24;
			for (var k = 0; k < 8; k++)
			{
				crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ poly : crc << 1;
			}
		}
	}
	if (refout) crc = Reflect32(crc);
	return crc ^ xorout;
}

static (byte[] Bytes, uint[] Words) MakeDecodedIntro(byte[] toc)
{
	var rawIntro = new byte[128];
	Array.Copy(toc, 0, rawIntro, 0, 128);
	var words = new uint[32];
	var span = new Span<uint>(words);
	DecodeXorIntro(rawIntro, span);
	var decoded = new byte[128];
	MemoryMarshal.AsBytes(span).CopyTo(decoded);
	return (decoded, words);
}

static void CrackWindowCore(byte[] seg, string name, uint target)
{
	var variants = new List<(string Name, uint Value)>();
	variants.Add(("crc32-std", Crc32Std(seg)));
	variants.Add(("crc32-init0", Crc32Init0(seg)));
	variants.Add(("crc32-noreflect", Crc32NoReflect(seg)));
	variants.Add(("crc32-noreflect-init0", Crc32NoReflectInit0(seg)));
	variants.Add(("crc32-castagnoli", Crc32Castagnoli(seg)));
	variants.Add(("crc16-fold-std", (uint)((ushort)Crc32Std(seg) + (Crc32Std(seg) >> 16))));
	variants.Add(("crc16-fold-lo", (uint)(ushort)Crc32Std(seg)));
	variants.Add(("crc16-fold-hi", Crc32Std(seg) >> 16));
	variants.Add(("sum32be", Sum32(seg, bigEndian: true)));
	variants.Add(("sum32le", Sum32(seg, bigEndian: false)));
	variants.Add(("xor32-all", XorAll(seg)));
	variants.Add(("sum16be", (uint)Sum16(seg, bigEndian: true)));
	variants.Add(("sum16le", (uint)Sum16(seg, bigEndian: false)));
	variants.Add(("adler32", Adler32(seg)));
	variants.Add(("fnv1a32", Fnv1a32(seg)));
	variants.Add(("sha256-loLE", HashTrunc(seg, "SHA256", takeLow: true, bigEndian: false)));
	variants.Add(("sha256-loBE", HashTrunc(seg, "SHA256", takeLow: true, bigEndian: true)));
	variants.Add(("sha256-hiLE", HashTrunc(seg, "SHA256", takeLow: false, bigEndian: false)));
	variants.Add(("sha256-hiBE", HashTrunc(seg, "SHA256", takeLow: false, bigEndian: true)));
	variants.Add(("sha1-loLE", HashTrunc(seg, "SHA1", takeLow: true, bigEndian: false)));
	variants.Add(("sha1-hiLE", HashTrunc(seg, "SHA1", takeLow: false, bigEndian: false)));
	variants.Add(("sha1-hiBE", HashTrunc(seg, "SHA1", takeLow: false, bigEndian: true)));
	variants.Add(("md5-loLE", HashTrunc(seg, "MD5", takeLow: true, bigEndian: false)));
	variants.Add(("md5-hiLE", HashTrunc(seg, "MD5", takeLow: false, bigEndian: false)));
	variants.Add(("md5-hiBE", HashTrunc(seg, "MD5", takeLow: false, bigEndian: true)));

	// reversed order variants (game may read table backwards / big-endian re-interpreted)
	var rev = new byte[seg.Length];
	for (var i = 0; i < seg.Length; i++) rev[i] = seg[seg.Length - 1 - i];
	variants.Add(("crc32-rev", Crc32Std(rev)));
	variants.Add(("crc32-noref-retrev", Crc32NoReflect(rev)));
	variants.Add(("sha256-lo-revLE", HashTrunc(rev, "SHA256", takeLow: true, bigEndian: false)));

	foreach (var v in variants)
	{
		if (v.Value == target)
		{
			Console.WriteLine($"  *** MATCH {name} len={seg.Length} via {v.Name} = 0x{v.Value:X8}");
		}
	}
}

static void CrackWindow(byte[] fullToc, string name, int start, int end, uint target)
{
	var seg = new byte[end - start];
	Array.Copy(fullToc, start, seg, 0, seg.Length);
	CrackWindowCore(seg, name, target);
}

static uint HashTrunc(ReadOnlySpan<byte> data, string algo, bool takeLow, bool bigEndian)
{
	byte[] hash;
	using (var h = System.Security.Cryptography.HashAlgorithm.Create(algo))
	{
		hash = h.ComputeHash(data.ToArray());
	}
	var idx = takeLow ? 0 : hash.Length - 4;
	var b = hash.AsSpan(idx, 4);
	return bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
}

static uint Crc32Castagnoli(ReadOnlySpan<byte> data)
{
	var crc = 0xFFFFFFFFu;
	foreach (var b in data)
	{
		crc ^= b;
		for (var k = 0; k < 8; k++)
		{
			crc = (crc & 1) != 0 ? 0x82F63B78u ^ (crc >> 1) : crc >> 1;
		}
	}
	return crc ^ 0xFFFFFFFFu;
}

static uint Crc32NoReflectInit0(ReadOnlySpan<byte> data)
{
	uint crc = 0;
	foreach (var b in data)
	{
		crc ^= (uint)b << 24;
		for (var k = 0; k < 8; k++)
		{
			crc = ((crc & 0x80000000u) != 0) ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
		}
	}
	return crc;
}

static ushort Sum16(ReadOnlySpan<byte> data, bool bigEndian)
{
	ulong sum = 0;
	for (var i = 0; i + 2 <= data.Length; i += 2)
	{
		if (bigEndian)
			sum += (ushort)(((uint)data[i] << 8) | data[i + 1]);
		else
			sum += (ushort)(data[i] | ((uint)data[i + 1] << 8));
	}
	return (ushort)sum;
}

static uint Adler32(ReadOnlySpan<byte> data)
{
	uint a = 1, b = 0;
	foreach (var d in data)
	{
		a = (a + d) % 65521;
		b = (b + a) % 65521;
	}
	return (b << 16) | a;
}

static uint Fnv1a32(ReadOnlySpan<byte> data)
{
	var h = 2166136261u;
	foreach (var d in data)
	{
		h ^= d;
		h *= 16777619u;
	}
	return h;
}

static uint Crc32Std(ReadOnlySpan<byte> data)
{
	var table = BuildCrc32Table();
	var crc = 0xFFFFFFFFu;
	for (var i = 0; i < data.Length; i++) crc = (crc >> 8) ^ table[(byte)(crc ^ data[i])];
	return crc ^ 0xFFFFFFFFu;
}

static uint Crc32Init0(ReadOnlySpan<byte> data)
{
	var table = BuildCrc32Table();
	var crc = 0u;
	for (var i = 0; i < data.Length; i++) crc = (crc >> 8) ^ table[(byte)(crc ^ data[i])];
	return crc;
}

static uint Crc32NoReflect(ReadOnlySpan<byte> data)
{
	// non-reflected CRC-32 (like MPEG-2 / POSIX cksum style with poly 0x04C11DB7)
	uint crc = 0xFFFFFFFFu;
	foreach (var b in data)
	{
		crc ^= (uint)b << 24;
		for (var k = 0; k < 8; k++)
		{
			crc = ((crc & 0x80000000u) != 0) ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
		}
	}
	return crc;
}

static uint FletcherWord(ReadOnlySpan<byte> data, bool littleEndian)
{
	var a = 0u;
	var b = 0u;
	for (var i = 0; i + 4 <= data.Length; i += 4)
	{
		var w = littleEndian
			? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4))
			: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i, 4));
		a = (a + w) % 0xFFFFFFFFu;
		b = (b + a) % 0xFFFFFFFFu;
	}
	return (b << 16) | (a & 0xFFFF);
}

static uint Fletcher32(ReadOnlySpan<byte> data, bool littleEndian)
{
	var a = 0u;
	var b = 0u;
	for (var i = 0; i + 2 <= data.Length; i += 2)
	{
		var w = littleEndian
			? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i, 2))
			: BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i, 2));
		a = (a + w) % 0xFFFFu;
		b = (b + a) % 0xFFFFu;
	}
	return (b << 16) | a;
}

static uint JenkinsOat(ReadOnlySpan<byte> data)
{
	var hash = 0u;
	foreach (var b in data)
	{
		hash += b;
		hash += hash << 10;
		hash ^= hash >> 6;
	}
	hash += hash << 3;
	hash ^= hash >> 11;
	hash += hash << 15;
	return hash;
}

static uint Murmur3x8632(ReadOnlySpan<byte> data)
{
	const uint c1 = 0xCC9E2D51, c2 = 0x1B873593;
	var h1 = 0u;
	var blockCount = data.Length / 4;
	for (var i = 0; i < blockCount; i++)
	{
		var k1 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4, 4));
		k1 *= c1;
		k1 = Rotl32(k1, 15);
		k1 *= c2;
		h1 ^= k1;
		h1 = Rotl32(h1, 13);
		h1 = h1 * 5 + 0xE6546B64u;
	}
	var k2 = 0u;
	switch (data.Length & 3)
	{
		case 3: k2 ^= (uint)data[blockCount * 4 + 2] << 16; goto case 2;
		case 2: k2 ^= (uint)data[blockCount * 4 + 1] << 8; goto case 1;
		case 1: k2 ^= data[blockCount * 4]; break;
	}
	if ((data.Length & 3) != 0)
	{
		k2 *= c1;
		k2 = Rotl32(k2, 15);
		k2 *= c2;
		h1 ^= k2;
	}
	h1 ^= (uint)data.Length;
	h1 ^= h1 >> 16;
	h1 *= 0x85EBCA6Bu;
	h1 ^= h1 >> 13;
	h1 *= 0xC2B2AE35u;
	h1 ^= h1 >> 16;
	return h1;
}

static uint Rotl32(uint v, int n) => (v << n) | (v >> (32 - n));

static uint Fnv1(ReadOnlySpan<byte> data)
{
	var hash = 0x811C9DC5u;
	for (var i = 0; i < data.Length; i++)
	{
		hash *= 0x01000193u;
		hash ^= data[i];
	}
	return hash;
}

static uint Sum32(ReadOnlySpan<byte> data, bool bigEndian)
{
	ulong sum = 0;
	for (var i = 0; i + 4 <= data.Length; i += 4)
	{
		if (bigEndian)
			sum += (uint)((uint)data[i] << 24 | (uint)data[i + 1] << 16 | (uint)data[i + 2] << 8 | data[i + 3]);
		else
			sum += BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4));
	}
	return (uint)sum;
}

static uint XorAll(ReadOnlySpan<byte> data)
{
	uint x = 0;
	for (var i = 0; i + 4 <= data.Length; i += 4)
		x ^= BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i, 4));
	return x;
}

static uint ReadUInt32At(FileStream fs, long pos)
{
	var buf = new byte[4];
	fs.Position = pos;
	fs.ReadExactly(buf, 0, 4);
	return BinaryPrimitives.ReadUInt32BigEndian(buf);
}

static ushort ReadUInt16At(FileStream fs, long pos)
{
	var buf = new byte[2];
	fs.Position = pos;
	fs.ReadExactly(buf, 0, 2);
	return BinaryPrimitives.ReadUInt16BigEndian(buf);
}

static NefsItem? FindItem(NefsArchive arch, string endsWith)
{
	return arch.Items.EnumerateById().FirstOrDefault(x =>
		arch.Items.GetItemFilePath(x.Id).EndsWith(endsWith, StringComparison.OrdinalIgnoreCase));
}

static (ulong Start, ushort Volume, ushort Flags, uint SharedInfo, uint FirstBlock, uint NextDuplicate) ReadEntry(
	FileStream fs, long entryTableOffset, long index)
{
	var buf = new byte[24];
	fs.Position = entryTableOffset + index * 24;
	fs.ReadExactly(buf, 0, 24);
	return (
		BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(0, 8)),
		BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(8, 2)),
		BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(10, 2)),
		BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(12, 4)),
		BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(16, 4)),
		BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(20, 4)));
}

static void WriteStart(FileStream fs, long pos, ulong value)
{
	Span<byte> buf = stackalloc byte[8];
	BinaryPrimitives.WriteUInt64BigEndian(buf, value);
	fs.Position = pos;
	fs.Write(buf);
}

static void WriteUInt32At(FileStream fs, long pos, uint value)
{
	Span<byte> buf = stackalloc byte[4];
	BinaryPrimitives.WriteUInt32BigEndian(buf, value);
	fs.Position = pos;
	fs.Write(buf);
}

static void WriteUInt16At(FileStream fs, long pos, ushort value)
{
	Span<byte> buf = stackalloc byte[2];
	BinaryPrimitives.WriteUInt16BigEndian(buf, value);
	fs.Position = pos;
	fs.Write(buf);
}

static uint[] BuildCrc32Table()
{
	var t = new uint[256];
	for (uint i = 0; i < 256; i++)
	{
		var c = i;
		for (var k = 0; k < 8; k++)
		{
			c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
		}
		t[i] = c;
	}
	return t;
}

static ushort Checksum16(byte[] data)
{
	var table = BuildCrc32Table();
	var crc = 0xFFFFFFFFu;
	foreach (var b in data)
	{
		crc = (crc >> 8) ^ table[(crc ^ b) & 0xFF];
	}
	crc = ~crc;
	return (ushort)((ushort)crc + (crc >> 16));
}

static void WriteUInt64At(FileStream fs, long pos, ulong value)
{
	Span<byte> buf = stackalloc byte[8];
	BinaryPrimitives.WriteUInt64BigEndian(buf, value);
	fs.Position = pos;
	fs.Write(buf);
}

static ulong ReadUInt64At(FileStream fs, long pos)
{
	var buf = new byte[8];
	fs.Position = pos;
	fs.ReadExactly(buf, 0, 8);
	return BinaryPrimitives.ReadUInt64BigEndian(buf);
}

static int NumChunks(long extractedSize, uint blockSize) =>
	(int)((extractedSize + blockSize - 1) / blockSize);

static uint[] ReadVerbatimEnds(FileStream fs, NefsHeader151 h, long firstBlock, int extractedSize, int blockSize)
{
	var nBlocks = (extractedSize + blockSize - 1) / blockSize;
	var ends = new uint[nBlocks];
	for (var b = 0; b < nBlocks; b++)
	{
		var row = h.Intro.BlockTableStart + (firstBlock + b) * 8;
		ends[b] = ReadUInt32At(fs, row);
	}
	return ends;
}

static (byte[] Data, uint[] CumEnds) CompressItem(byte[] input, int chunkSize)
{
	using var outMem = new MemoryStream();
	var ends = new List<uint>();
	var nChunks = (input.Length + chunkSize - 1) / chunkSize;
	uint cum = 0;
	for (var i = 0; i < nChunks; ++i)
	{
		var n = Math.Min(chunkSize, input.Length - i * chunkSize);
		long before = outMem.Length;
		using (var inMem = new MemoryStream(input, i * chunkSize, n))
		using (var deflate = new DeflateStream(outMem, CompressionMode.Compress, leaveOpen: true))
		{
			inMem.CopyTo(deflate);
			deflate.Close();
		}

		cum += (uint)(outMem.Length - before);
		ends.Add(cum);
	}
	return (outMem.ToArray(), ends.ToArray());
}

static async Task<bool> VerifyRelayoutAsync(NefsReader reader, string volumeFile, string[] names, byte[][] expected)
{
	var arch = await reader.ReadArchiveAsync(volumeFile, new NefsProgress());
	for (var i = 0; i < names.Length; i++)
	{
		var item = FindItem(arch, names[i]);
		if (item is null)
		{
			return false;
		}

		var (_, content) = ExtractItemAsync(arch, item);
		var ok = content.Length == expected[i].Length && content.SequenceEqual(expected[i]);
		Console.WriteLine($"re-extracted {names[i]}: {content.Length} bytes (expected {expected[i].Length}) -> {(ok ? "OK" : "MISMATCH")}");
		if (!ok)
		{
			return false;
		}
	}

	return true;
}

static async Task<bool> VerifyNamedItemsAsync(NefsReader reader, string volumeFile, Dictionary<string, byte[]> expectedByName)
{
	var arch = await reader.ReadArchiveAsync(volumeFile, new NefsProgress());
	foreach (var kv in expectedByName)
	{
		var item = FindItem(arch, kv.Key);
		if (item is null)
		{
			Console.WriteLine($"re-extracted {kv.Key}: NOT FOUND -> MISMATCH");
			return false;
		}

		var (_, content) = ExtractItemAsync(arch, item);
		var ok = content.Length == kv.Value.Length && content.SequenceEqual(kv.Value);
		Console.WriteLine($"re-extracted {kv.Key}: {content.Length} bytes (expected {kv.Value.Length}) -> {(ok ? "OK" : "MISMATCH")}");
		if (!ok)
		{
			return false;
		}
	}

	return true;
}

static async Task<bool> VerifyExtractAsync(NefsReader reader, string volumeFile, byte[] engBytes, byte[] useBytes)
{
	var arch = await reader.ReadArchiveAsync(volumeFile, new NefsProgress());
	var eng = FindItem(arch, "language_eng.lng");
	var use = FindItem(arch, "language_use.lng");
	if (eng is null || use is null)
	{
		return false;
	}

	var (_, engOut) = ExtractItemAsync(arch, eng);
	var (_, useOut) = ExtractItemAsync(arch, use);
	Console.WriteLine($"re-extracted eng: {engOut.Length} bytes (expected {engBytes.Length})");
	Console.WriteLine($"re-extracted use: {useOut.Length} bytes (expected {useBytes.Length})");
	return engOut.Length == engBytes.Length
		&& useOut.Length == useBytes.Length
		&& engOut.SequenceEqual(engBytes)
		&& useOut.SequenceEqual(useBytes);
}

static (long Length, byte[] Content) ExtractItemAsync(NefsArchive arch, NefsItem item)
{
	var chunks = item.DataSource.Size.Chunks;
	if (chunks.Count == 0)
	{
		return (0, Array.Empty<byte>());
	}

	var filePath = item.DataSource.FilePath;
	using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
	using var outMem = new MemoryStream();
	long lastCum = 0;
	foreach (var chunk in chunks)
	{
		var chunkSize = (int)(chunk.CumulativeSize - lastCum);
		var buf = new byte[chunkSize];
		fs.Position = item.DataSource.Offset + lastCum;
		fs.ReadExactly(buf, 0, chunkSize);
		using var inMem = new MemoryStream(buf, 0, chunkSize, writable: false);
		using var inflate = new DeflateStream(inMem, CompressionMode.Decompress);
		inflate.CopyTo(outMem);
		lastCum = chunk.CumulativeSize;
	}
	return (outMem.Length, outMem.ToArray());
}

static long FindFreeSpace(List<(long start, long end)> used, long fileLen, long minStart, long need)
{
	used.Sort((a, b) => a.start.CompareTo(b.start));
	long cursor = minStart;
	foreach (var (s, e) in used)
	{
		if (e < cursor)
		{
			continue;
		}
		if (s - cursor >= need)
		{
			return cursor;
		}
		cursor = Math.Max(cursor, e);
	}
	return fileLen - cursor >= need ? cursor : -1;
}