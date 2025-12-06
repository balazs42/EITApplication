using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Utility.Classes.PostProcessing
{
    public class PostProcessingGroup : ObservableCollection<IPostProcessing>
    {
        public string Title { get; }
        public string Description { get; }

        public PostProcessingGroup(string title, string description, IEnumerable<IPostProcessing> options) : base(options)
        {
            Title = title;
            Description = description;
        }
    }
}
