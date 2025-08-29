namespace Utility.Classes.Application
{
    /// <summary>
    /// Default User is the regular user, who can configure reconstruction, load measuremnets, and try several
    /// reconstruction techniques. The default user does not have any further configuration access.
    /// </summary>
    public class DefaultUser : User
    {

        public DefaultUser(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public DefaultUser(int id, string name, string email)
        {
            Id = id;
            Name = name;
            Email = email;
        }
    }
}
