using Jellyfin.Plugin.EdemRefresh;
using System.Xml.Linq;
var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
try {
var now = new DateTimeOffset(2026,10,2,12,0,0,TimeSpan.Zero);
var guide=Path.Combine(dir,"source.xml");
File.WriteAllText(guide,"""
<tv><channel id="1"><display-name>Первый канал</display-name></channel><channel id="2"><display-name>Ambiguous</display-name></channel><channel id="3"><display-name>Ambiguous</display-name></channel>
<programme channel="1" start="20261002120000 +0000" stop="20261002130000 +0000"><title>Movie</title><category>Фильм</category></programme>
<programme channel="1" start="20261002130000 +0000" stop="20261002140000 +0000"><title>Программа не найдена.</title></programme>
<programme channel="1" start="20260901130000 +0000" stop="20260901140000 +0000"><title>Expired</title></programme></tv>
""");
var m3u="""
#EXTM3U
#EXTINF:-1 tvg-logo="https://example.org/a,b.png", Первый канал
#EXTGRP:Общие
http://example.org/stream1
#EXTINF:-1 group-title="Other",Ambiguous
http://example.org/stream2
#EXTINF:-1 tvg-id="1",Renamed
http://example.org/stream3
""";
Pipeline.Convert(m3u,guide,dir,now);
var converted=File.ReadAllText(Path.Combine(dir,"playlist.m3u"));
void Check(bool condition) { if (!condition) throw new Exception("Check failed"); }
Check(converted.Contains("group-title=\"Общие\"")); Check(!converted.Contains("#EXTGRP"));
Check(converted.Split("tvg-id=\"1\"").Length==3); Check(converted.Contains("tvg-logo=\"https://example.org/a,b.png\""));
Check(m3u.Split('\n').Where(l=>l.StartsWith("http")).SequenceEqual(converted.Split('\n').Where(l=>l.StartsWith("http"))));
var xml=XDocument.Load(Path.Combine(dir,"guide.xml")); Check(xml.Root!.Elements("programme").Count()==1); Check(xml.Descendants("category").Any(x=>x.Value=="movie"));
bool refused=false; try { Pipeline.Convert("<html>error</html>",guide,dir,now); } catch(InvalidDataException) {refused=true;} Check(refused);
File.WriteAllText(guide,"<!DOCTYPE tv [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><tv>&x;</tv>");
refused=false; try { Pipeline.Convert(m3u,guide,dir,now); } catch(System.Xml.XmlException) {refused=true;} Check(refused);
Console.WriteLine("PASS: groups, exact IDs, ambiguous matches, existing IDs, URLs, category labels, placeholders, expiry, malformed input and XXE rejection");
if(args.Length==2){var original=File.ReadAllText(args[0]); Console.WriteLine(Pipeline.Convert(original,args[1],dir,DateTimeOffset.UtcNow));
Check(original.Replace("\r", "").Split('\n').Where(l=>l.Length>0&&!l.StartsWith('#')).SequenceEqual(File.ReadAllLines(Path.Combine(dir,"playlist.m3u")).Where(l=>l.Length>0&&!l.StartsWith('#'))));}
} finally { Directory.Delete(dir,true); }
