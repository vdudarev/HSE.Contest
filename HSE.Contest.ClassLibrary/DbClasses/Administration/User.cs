﻿using HSE.Contest.ClassLibrary.DbClasses.TestingSystem;
using Microsoft.AspNetCore.Identity;
using System.Collections.Generic;

namespace HSE.Contest.ClassLibrary.DbClasses.Administration
{
    public class User : IdentityUser
    {        
        public string FirstName { get; set; }
        public string LastName { get; set; }      
        public virtual List<UserGroup> Groups { get; set; } = new List<UserGroup>();
        public virtual List<StudentResult> Results { get; set; } = new List<StudentResult>();
    }
}
