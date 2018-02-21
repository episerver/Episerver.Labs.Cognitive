using System;
using EPiServer.Core;
using EPiServer.PlugIn;
using EPiServer.Scheduler;
using EPiServer;
using EPiServer.ServiceLocation;
using EPiServer.DataAbstraction;
using System.Linq;
using EPiServer.DataAccess;
using System.Threading;

namespace Episerver.Labs.Cognitive
{
    [ScheduledPlugIn(DisplayName = "Vision Tagging")]
    public class VisionTaggingJob : ScheduledJobBase
    {
        private bool _stopSignaled;
        protected Injected<IContentRepository> repo { get; set; }
        protected Injected<IContentModelUsage> cmu { get; set; }

        protected Injected<IContentTypeRepository> typerepo { get; set; }

        public VisionTaggingJob()
        {
            IsStoppable = true;
        }

        /// <summary>
        /// Called when a user clicks on Stop for a manually started job, or when ASP.NET shuts down.
        /// </summary>
        public override void Stop()
        {
            _stopSignaled = true;
        }

        /// <summary>
        /// Called when a scheduled job executes
        /// </summary>
        /// <returns>A status message to be stored in the database log and visible from admin mode</returns>
        public override string Execute()
        {
            //Call OnStatusChanged to periodically notify progress of job for manually started jobs
            OnStatusChanged("Preparing to auto-tag all images");

            int cnt = 0;
            //Add implementation
            var handler = new VisionHandler();
            if (handler.Enabled)
            {
                //Get images recursive
                var clist = typerepo.Service.List().Where(ct => ct.ModelType.IsSubclassOf(typeof(ImageData))).Where(ct => ct.ModelType.PropertiesWithVisionAttributes().Any())
                    .SelectMany(ct => cmu.Service.ListContentOfContentType(ct));
                
                //Check if image has any properties that needs handling
                foreach(var imgusage in clist)
                {
                    var img = (ImageData)repo.Service.Get<ImageData>(imgusage.ContentLink).CreateWritableClone();
                    handler.HandleImage(img);//If so, get a writeable copy and handle them.
                    var action = SaveAction.CheckIn;
                    if (img.Status == VersionStatus.Published) action = SaveAction.Publish;
                    repo.Service.Save(img, action, EPiServer.Security.AccessLevel.NoAccess); //Save | Publish
                    cnt++;
                    OnStatusChanged($"Handled {cnt} images ");
                    if (_stopSignaled) return "Job stopped - " + cnt.ToString() + " images tagged.";
                    //At the most 20 calls per minute, let's sleep for 10 sec just to be sure. TODO: Make optional.
                    Thread.Sleep(10000);
                }
                
                
            } else
            {
                return "Vision is not enabled in app settings";
            }
            //For long running jobs periodically check if stop is signaled and if so stop execution
            if (_stopSignaled)
            {
                return "Stop of job was called";
            }

            return "Completed tagging "+cnt.ToString()+" images";
        }
    }
}
