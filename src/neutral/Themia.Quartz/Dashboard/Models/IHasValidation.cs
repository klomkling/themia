using System.Collections.Generic;

namespace Themia.Quartz.Dashboard.Models
{
    public interface IHasValidation
    {
        void Validate(ICollection<ValidationError> errors);
    }
}