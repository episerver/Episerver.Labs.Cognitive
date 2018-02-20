using Episerver.Labs.Cognitive.Attributes;
using EPiServer.Core;
using EPiServer.Framework.Blobs;
using EPiServer.ServiceLocation;
using Microsoft.ProjectOxford.Vision;
using Microsoft.ProjectOxford.Vision.Contract;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Episerver.Labs.Cognitive
{
    public class VisionHandler
    {
        public const string APPSETTINGS_VISION_KEY = "Vision:Key";
        public const string APPSETTINGS_VISION_APIROOT = "Vision:ApiRoot";

        protected Injected<IBlobFactory> blobfactory { get; set; }

        protected Microsoft.ProjectOxford.Vision.VisionServiceClient Client { get; set; }

        public bool Enabled
        {
            get
            {
                return (Client != null);
            }
        }


        public void HandleImage(ImageData img)
        {
            var thumbs = img.GetEmptyPropertiesWithAttribute(typeof(SmartThumbnailAttribute));
            //TODO: Solve problem here.
            var lst = img.GetEmptyPropertiesWithAttribute(typeof(VisionAttribute)).ToList();
            var ocrs = img.GetEmptyPropertiesWithAttribute(typeof(VisionAttribute)).Where(prop => prop.GetCustomAttributes().Where(ca => ca is VisionAttribute).Cast<VisionAttribute>().First().VisionType == VisionTypes.Text).ToList();
            var descriptions = img.GetEmptyPropertiesWithAttribute(typeof(VisionAttribute)).Where(prop => prop.GetCustomAttributes().Where(ca => ca is VisionAttribute).Cast<VisionAttribute>().First().VisionType != VisionTypes.Text).ToList();
            if (thumbs.Any() || ocrs.Any() || descriptions.Any())
            {
                //Size image properly
                Stream strm = img.BinaryData.OpenRead();
                if (strm.Length >= 4000000)
                {
                    var g = ScaleImage(Image.FromStream(strm), 500, 500); //TODO: Change this to make largest possible size smaller than 4 mb
                    strm = new MemoryStream(4000000);
                    g.Save(strm, System.Drawing.Imaging.ImageFormat.Jpeg);
                    strm.Seek(0, SeekOrigin.Begin);
                }
                BinaryReader reader = new BinaryReader(strm);
                byte[] bytes=reader.ReadBytes((int) strm.Length);
                reader.Close();
                //Handle thumbnails
                List<Task> tasks = new List<Task>();
                if (thumbs.Any()) tasks.Add(Task.Run(()=>GenerateThumbnails(img, new MemoryStream(bytes), thumbs)));//tasks.Add(GenerateThumbnails(img, strm, thumbs));

                //handle OCR
                if (ocrs.Any()) tasks.Add(Task.Run(()=>GenerateOCR(img, new MemoryStream(bytes), ocrs)));

                //handle descriptions
                if (descriptions.Any()) tasks.Add(Task.Run(()=>TagAndDescripe(img,new MemoryStream(bytes),descriptions)));

                //Complete all.

                Task.WaitAll(tasks.ToArray());
            }
        }

        public async Task TagAndDescripe(ImageData img, Stream strm, IEnumerable<PropertyInfo> props)
        {
            var res = await AnalyzeImage(strm);
            foreach (var p in props)
            {
                var atb = p.GetCustomAttribute<VisionAttribute>();
                switch (atb.VisionType)
                {
                    case VisionTypes.Adult:
                        if (p.PropertyType == typeof(bool))
                        {
                            p.SetValue(img, res.Adult.IsAdultContent);
                        } else if (p.PropertyType == typeof(double))
                        {
                            p.SetValue(img, res.Adult.AdultScore);
                        }
                        break;
                    case VisionTypes.Categories:
                        var catlist=res.Categories.Select(c => c.Name).ToArray();
                        if (p.PropertyType == typeof(string[]))
                        {
                            p.SetValue(img, catlist);
                        } else if(p.PropertyType == typeof(string))
                        {
                            p.SetValue(img, string.Join(atb.Separator, catlist));
                        }
                        break;
                    case VisionTypes.ClipArt:
                        if (p.PropertyType == typeof(bool))
                        {
                            p.SetValue(img, (res.ImageType.ClipArtType == 0));
                        }
                        break;
                    case VisionTypes.Description:
                        //Handle string array with multiple captions.
                        if (p.PropertyType == typeof(string))
                        {
                            p.SetValue(img, res.Description.Captions.Select(d => d.Text).FirstOrDefault());
                        } else if (p.PropertyType == typeof(string[]))
                        {
                            p.SetValue(img, res.Description.Captions.Select(d => d.Text).ToArray());
                        }
                        break;
                    case VisionTypes.LineDrawing:
                        if (p.PropertyType == typeof(bool))
                        {
                            p.SetValue(img, (res.ImageType.LineDrawingType == 0));
                        }
                        break;
                    case VisionTypes.Racy:
                        if (p.PropertyType == typeof(bool))
                        {
                            p.SetValue(img, res.Adult.IsRacyContent);
                        }
                        else if (p.PropertyType == typeof(double))
                        {
                            p.SetValue(img, res.Adult.RacyScore);
                        }
                        break;
                    case VisionTypes.Tags:
                        if(p.PropertyType == typeof(string))
                        {
                            p.SetValue(img, string.Join(atb.Separator, res.Tags.Select(t => t.Name).ToArray()));
                        } else if(p.PropertyType == typeof(string[]))
                        {
                            p.SetValue(img, res.Tags.Select(t => t.Name).ToArray());
                        } //TODO: xhtml, category field.
                        break;
                    case VisionTypes.BlackAndWhite:
                        if (p.PropertyType == typeof(bool))
                        {
                            p.SetValue(img, res.Color.IsBWImg);
                        }
                        break;
                    case VisionTypes.AccentColor:
                        if (p.PropertyType == typeof(string))
                        {
                            p.SetValue(img, res.Color.AccentColor);
                        }
                        break;
                    case VisionTypes.DominantBackgroundColor:
                        if (p.PropertyType == typeof(string))
                        {
                            p.SetValue(img, res.Color.DominantColorBackground);
                        }
                        break;
                    case VisionTypes.DominantForegroundColor:
                        if (p.PropertyType == typeof(string))
                        {
                            p.SetValue(img, res.Color.DominantColorForeground);
                        }
                        break;
                    case VisionTypes.Faces:
                        var fcs = res.Faces.Select(fc => fc.Gender + " " + fc.Age.ToString()).ToArray();
                        //TODO: Handle if no faces, so it won't fire again.
                        if (p.PropertyType == typeof(string))
                        {
                            p.SetValue(img,string.Join(atb.Separator, fcs));
                        } else if (p.PropertyType == typeof(string[]))
                        {
                            p.SetValue(img, fcs);
                        }
                        break;
                    case VisionTypes.FacesAge:
                        var ages = res.Faces.Select(fc => fc.Age.ToString()).ToArray();
                        if (p.PropertyType == typeof(string))
                        {
                            p.SetValue(img, string.Join(atb.Separator, ages));
                        }
                        else if (p.PropertyType == typeof(string[]))
                        {
                            p.SetValue(img, ages);
                        }
                        break;
                    case VisionTypes.FacesGender:
                        var genders = res.Faces.Select(fc => fc.Gender).ToArray();
                        if (p.PropertyType == typeof(string))
                        {
                            p.SetValue(img, string.Join(atb.Separator, genders));
                        }
                        else if (p.PropertyType == typeof(string[]))
                        {
                            p.SetValue(img, genders);
                        }
                        break;
                    default:
                        break;
                }
            }
            return;
        }

        public async Task GenerateOCR(ImageData img, Stream strm, IEnumerable<PropertyInfo> props)
        {
            var ocrres = await IdentifyText(strm);
            var txt=ocrres.Regions.SelectMany(r => r.Lines).Select(l => string.Join(" ", l.Words.Select(w => w.Text).ToArray())).ToArray();
            foreach (var p in props)
            {
                var atb = p.GetCustomAttribute<VisionAttribute>();
                if (p.PropertyType == typeof(string[]))
                {
                    //return multiple lines
                    p.SetValue(img, txt);
                } else if(p.PropertyType == typeof(string))
                {
                    //Combine into 1 line
                    p.SetValue(img, string.Join(atb.Separator, txt));
                } else if(p.PropertyType == typeof(XhtmlString))
                {
                    //TODO: Check if this is even called? If so, build html based representation of the regions.
                    string s = string.Join("",txt.Select(t => "<p>" + t + "</p>"));
                    p.SetValue(img, new XhtmlString(s));
                }
            }
        }
        
        public async Task GenerateThumbnails(ImageData img, Stream strm, IEnumerable<PropertyInfo> props)
        {
            foreach(var p in props)
            {
                if (p.PropertyType == typeof(Blob))
                {
                    var atb = p.GetCustomAttribute<SmartThumbnailAttribute>();
                    var bytes = await MakeSmartThumbnail(strm, atb.Width, atb.Height);
                    var blob = blobfactory.Service.CreateBlob(img.BinaryDataContainer, Path.GetExtension(img.BinaryData.ID.ToString()));
                    Stream outstream = blob.OpenWrite();
                    outstream.Write(bytes, 0, bytes.Length);
                    outstream.Close();
                    p.SetValue(img, blob);
                }
            }
        }


        public VisionHandler()
        {
            if (ConfigurationManager.AppSettings.AllKeys.Contains(APPSETTINGS_VISION_KEY) 
                && ConfigurationManager.AppSettings.AllKeys.Contains(APPSETTINGS_VISION_APIROOT))
            {
                Client = new VisionServiceClient(ConfigurationManager.AppSettings[APPSETTINGS_VISION_KEY],
                    ConfigurationManager.AppSettings[APPSETTINGS_VISION_APIROOT]);
            }
        }

        public VisionHandler(string Key)
        {
            Client = new Microsoft.ProjectOxford.Vision.VisionServiceClient(Key);
        }

        public async Task<OcrResults> IdentifyText(Stream s)
        {
            s.Seek(0, SeekOrigin.Begin);
            var imgres = await Client.RecognizeTextAsync(s);
            return imgres;
        }

        public async Task<AnalysisResult> AnalyzeImage(Stream s)
        {
            try
            {
                s.Seek(0, SeekOrigin.Begin);
                var img = await Client.AnalyzeImageAsync(s, visualFeatures: new VisualFeature[] { VisualFeature.Description, VisualFeature.Tags, VisualFeature.Adult, VisualFeature.Categories, VisualFeature.ImageType, VisualFeature.Color, VisualFeature.Faces });
                return img;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        public async Task<byte[]> MakeSmartThumbnail(Stream s, int x, int y)
        { 
            s.Seek(0, SeekOrigin.Begin);
            return await Client.GetThumbnailAsync(s, x, y);
        }
        
        //TODO: Resize so max size is <4 MB. Guess which scale that is based on current bytesize and w+h.
        public static Image ScaleImage(Image image, int maxWidth, int maxHeight)
        {
            var ratioX = (double)maxWidth / image.Width;
            var ratioY = (double)maxHeight / image.Height;
            var ratio = Math.Min(ratioX, ratioY);
            var newWidth = (int)(image.Width * ratio);
            var newHeight = (int)(image.Height * ratio);
            var newImage = new Bitmap(newWidth, newHeight);
            using (var graphics = Graphics.FromImage(newImage))
                graphics.DrawImage(image, 0, 0, newWidth, newHeight);
            return newImage;
        }
    }
}
