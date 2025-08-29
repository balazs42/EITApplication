namespace Utility.Classes.Application
{
    public abstract class User
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public int Id { get; set; } = -1;

        public User()
        {
            Name = "Test";
            Email = "test@gmail.com";
            Id = 0;
        }

        public User(string name, string email, int id)
        {
            Name = name;
            Email = email;
            Id = id;    
        }
    }
}
